using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Canonical facet state. The commit queue orders cold reads, reduction, durability and publication.</summary>
public sealed class CatalogRepository
{
    sealed class Entry(ResourceSnapshot snapshot)
    {
        public ResourceSnapshot Snapshot = snapshot;
        public bool Loaded;
        public long RequestId;
        public long EstimatedBytes = CatalogResidentSize.Estimate(snapshot.Key, snapshot.Value);
    }

    readonly DataCommitQueue _commits;
    readonly ICatalogPersistence _persistence;
    readonly TimeProvider _time;
    readonly object _gate = new();
    readonly Dictionary<ResourceKey, Entry> _entries = new();
    // A full query pass Peeks thousands of keys the repository has never fetched (optional facets on offscreen
    // rows). Each miss used to allocate a fresh ResourceSnapshot.Unknown(key); a per-key cache turns a whole pass's
    // worth of misses into a handful of allocations. ResourceSnapshot is an immutable record, so sharing the
    // instance across callers is safe. Guarded by _gate; evicted the moment a real entry lands for the key, and
    // cleared wholesale by TrimUnpinned.
    readonly Dictionary<ResourceKey, ResourceSnapshot> _unknown = new();
    readonly CatalogChanges _changes = new();
    // ── the lock-free render view ────────────────────────────────────────────────────────────────────────────────
    // A frame must never take _gate. It is the SAME gate a whole-membership join holds for its entire duration, and
    // the render path took it once per rendered row (VideoPresence.HasVideo) and once per track in the detail
    // mappers — so a 1494-row join stalled every frame that touched a row. Every mutation ends at PublishKeys, so
    // that one funnel republishes an immutable copy of the facets the render path actually probes; a reader takes
    // the reference with a Volatile.Read and answers from it with no lock at all.
    ImmutableDictionary<ResourceKey, ResourceSnapshot> _renderView = ImmutableDictionary<ResourceKey, ResourceSnapshot>.Empty;
    CatalogScope _publishedScope;
    CatalogScope _scope;
    string _actualAccount;
    long _epoch = 1, _nextRequest, _revision;
    long _residentBytes;
    long _lastColdReadMs;
    bool _online;

    /// <param name="online">Overrides the derived connection state. A launch that recalls its last session's scope
    /// (the provisional scope: it names a context, and every cached row is already keyed by it) constructs an
    /// authenticated-LOOKING scope with no connection behind it yet, and passes false — the same state
    /// <see cref="SetOfflineCore"/> leaves behind. Null derives it as before.</param>
    public CatalogRepository(DataCommitQueue commits, ICatalogPersistence persistence, TimeProvider time,
        CatalogScope scope, string actualAccount, bool? online = null)
    {
        _commits = commits ?? throw new ArgumentNullException(nameof(commits));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _scope = _publishedScope = scope ?? throw new ArgumentNullException(nameof(scope));
        _actualAccount = actualAccount ?? throw new ArgumentNullException(nameof(actualAccount));
        _online = online ?? (scope.ContextKnown && actualAccount.Length > 0 && scope.ProviderAccount == actualAccount);
    }

    public CatalogScope Scope { get { lock (_gate) return _scope; } }

    /// <summary>The same value <see cref="Scope"/> answers, without taking the gate — for the render path, which
    /// asks for it once per row. Republished under the gate by every session change, so a reader is at worst one
    /// publication behind, exactly like <see cref="TryPeekPublished"/>.</summary>
    public CatalogScope PublishedScope => Volatile.Read(ref _publishedScope);

    /// <summary>Lock-free resident lookup for the render path (see the render view above). Only the facets a frame
    /// probes are carried — <see cref="RenderFacet"/> — so an unlisted facet always answers false here and must go
    /// through <see cref="TryPeek"/>.</summary>
    public bool TryPeekPublished(ResourceKey key, out ResourceSnapshot snapshot)
    {
        if (Volatile.Read(ref _renderView).TryGetValue(key, out var found)) { snapshot = found; return true; }
        snapshot = null!; return false;
    }

    /// <summary>Which facets the lock-free view carries. Video association is the one fact a rendered row asks the
    /// catalog for directly (every other row fact travels on the query's own publication).</summary>
    static bool RenderFacet(FacetKind facet) => facet == FacetKind.VideoAssociation;
    public long Epoch { get { lock (_gate) return _epoch; } }
    public bool IsOnline { get { lock (_gate) return _online; } }
    /// <summary>Duration of the most recent persistence round-trip that actually loaded unread keys.
    /// Zero when every key in the last <see cref="ReadManyAsync"/> was already resident.</summary>
    public long LastColdReadMs => Volatile.Read(ref _lastColdReadMs);
    public bool IsCurrent(ResourceRequest request)
    {
        lock (_gate) return Matches(request.Key, request.Stamp) && RequestAllowed(request.Key.Scope, request.RequiresNetwork);
    }
    public IObservable<CatalogChangeSet> Changes => _changes;
    public int ResidentCount { get { lock (_gate) return _entries.Count; } }
    public long EstimatedResidentBytes { get { lock (_gate) return _residentBytes; } }

    // Reused across every trim look instead of a fresh HashSet per call (a busy sync coalesces many looks a
    // second into one per TrimQuietMs, but each one used to pay a ~1.7k-entry allocation just to ask "what's
    // pinned").
    readonly HashSet<ResourceKey> _trimRetained = new();

    /// <param name="collectPins">Writes the currently pinned (actively demanded) keys into the given set — the
    /// set is cleared first, so the callee need not.</param>
    public long TrimUnpinned(Action<HashSet<ResourceKey>> collectPins, int maximum)
    {
        ArgumentNullException.ThrowIfNull(collectPins);
        long freed = 0;
        _commits.Publish(() =>
        {
            // Demand changes use the same publication gate. Capture pins here, not before waiting for it.
            _trimRetained.Clear();
            collectPins(_trimRetained);
            var removed = new List<ResourceKey>();
            lock (_gate)
            {
                int excess = _entries.Count - maximum;
                if (excess > 0)
                {
                    // The k oldest eligible entries by FetchedAt, found with a bounded max-heap instead of sorting
                    // the whole resident table: O(n log k) instead of O(n log n), and no full-table array copy.
                    var oldest = new PriorityQueue<ResourceKey, long>();
                    foreach (var pair in _entries)
                    {
                        if (_trimRetained.Contains(pair.Key)) continue;
                        var snapshot = pair.Value.Snapshot;
                        if (snapshot.Activity is not (ResourceActivity.Idle or ResourceActivity.Offline) || !pair.Value.Loaded) continue;
                        oldest.Enqueue(pair.Key, -snapshot.FetchedAt.Ticks);
                        if (oldest.Count > excess) oldest.Dequeue();
                    }
                    while (oldest.TryDequeue(out var key, out _))
                    {
                        var bytes = _entries[key].EstimatedBytes;
                        _entries.Remove(key); _residentBytes -= bytes; freed += bytes; removed.Add(key);
                    }
                }
                _unknown.Clear();
            }
            if (removed.Count > 0) PublishKeys(removed, CatalogChangeKind.Activity);
        });
        return freed;
    }

    internal void ForgetEvicted(IReadOnlyList<ResourceKey> keys)
    {
        _commits.Publish(() =>
        {
            lock (_gate) foreach (var key in keys)
                if (_entries.Remove(key, out var entry)) _residentBytes -= entry.EstimatedBytes;
            PublishKeys(keys);
        });
    }

    /// <summary>A pure query read holds one publication across catalog facts and the replica references it joins.</summary>
    public T ReadConsistent<T>(Func<T> read, [System.Runtime.CompilerServices.CallerMemberName] string label = "")
        => _commits.ReadConsistent(() => { lock (_gate) return read(); }, label);

    /// <summary>Pure resident lookup for rendering paths; an unknown facet returns false without allocating a placeholder.</summary>
    public bool TryPeek(ResourceKey key, out ResourceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry)) { snapshot = entry.Snapshot; return true; }
            snapshot = null!; return false;
        }
    }

    public ResourceSnapshot Peek(ResourceKey key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry)) return entry.Snapshot;
            if (_unknown.TryGetValue(key, out var cached)) return cached;
            return _unknown[key] = ResourceSnapshot.Unknown(key);
        }
    }

    public async Task<ResourceSnapshot> ReadAsync(ResourceKey key, CancellationToken ct = default)
    {
        await PreloadAsync([key], ct).ConfigureAwait(false);
        return Peek(key);
    }

    /// <summary>Cold-loads every key off the commit worker, then answers from memory. The peek needs the loaded
    /// snapshots, never the queue's ordering — a page of keys must not hold the single writer while it reads.</summary>
    public async Task<IReadOnlyList<ResourceSnapshot>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await PreloadAsync(keys, ct).ConfigureAwait(false);
        var snapshots = new ResourceSnapshot[keys.Count];
        for (int i = 0; i < keys.Count; i++) snapshots[i] = Peek(keys[i]);
        return snapshots;
    }

    public bool CanRequest(ResourceKey key, bool requiresNetwork)
    { lock (_gate) return ActiveScope(key.Scope) && RequestAllowed(key.Scope, requiresNetwork); }

    /// <summary>Whether <paramref name="scope"/> is still the repository's live scope — the "superseded" test for a
    /// query node whose resources were read under a scope the session has since moved away from. Local facts (a
    /// non-Spotify provider) are active under the current storage/context even before Spotify authentication; this
    /// grants no transport access, it only says the entries are not stale.</summary>
    public bool IsActiveScope(CatalogScope scope) { lock (_gate) return ActiveScope(scope); }

    public async Task<ResourceRequest> CaptureRequestAsync(ResourceKey key, ResourcePriority priority,
        string? clientFeatureId = null, bool requiresNetwork = true, CancellationToken ct = default)
        => (await CaptureRequestsAsync([key], priority, clientFeatureId, requiresNetwork, ct).ConfigureAwait(false))[0];

    public Task<IReadOnlyList<ResourceRequest>> CaptureRequestsAsync(IReadOnlyList<ResourceKey> keys,
        ResourcePriority priority, string? clientFeatureId = null, bool requiresNetwork = true, CancellationToken ct = default)
        => _commits.ExecuteAsync(_ =>
        {
            var requests = new ResourceRequest[keys.Count];
            lock (_gate)
                for (int i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    var entry = EntryOf(key);
                    entry.RequestId = ++_nextRequest;
                    requests[i] = new ResourceRequest(key,
                        new RequestStamp(key.Scope, _actualAccount, _epoch, entry.Snapshot.Generation, entry.RequestId),
                        priority, clientFeatureId, requiresNetwork);
                }
            return ValueTask.FromResult((IReadOnlyList<ResourceRequest>)requests);
        }, ct);

    public Task SetSessionAsync(CatalogScope scope, string actualAccount, bool online, CancellationToken ct = default)
        => _commits.ExecuteAsync(_ =>
        {
            _commits.Publish(() => SetSessionCore(scope, actualAccount, online));
            return ValueTask.FromResult(true);
        }, ct);

    /// <summary>Installs a session. Returns whether it merely CONFIRMED the owner already in place (see
    /// <see cref="SessionInstallRules.ConfirmsOwner"/>) — the caller re-stamps the durable replicas instead of reloading
    /// them when it did.</summary>
    internal bool SetSessionCore(CatalogScope scope, string actualAccount, bool online)
    {
        if (!_commits.IsExecuting) throw new InvalidOperationException("Session changes require the shared commit owner.");
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actualAccount);
        if (online && (!scope.ContextKnown || actualAccount.Length == 0 || scope.ProviderAccount != actualAccount))
            throw new ArgumentException("An online catalog scope must name the authenticated provider account.", nameof(scope));
        bool owner, confirmed;
        ResourceKey[] keys;
        lock (_gate)
        {
            owner = SessionInstallRules.ConfirmsOwner(_scope, _actualAccount, scope, actualAccount, online);
            confirmed = SessionInstallRules.ConfirmsScope(_scope, _actualAccount, scope, actualAccount, online);
            _scope = scope; _actualAccount = actualAccount; _online = online; _epoch++;
            Volatile.Write(ref _publishedScope, scope);
            // A REPLACEMENT re-keys everything (the scope is part of every ResourceKey), so every resident answer is
            // republished and every leased node re-joins. A CONFIRMATION is the AP welcome certifying the provisional
            // scope this launch already served: the keys are identical and the answers under them are still this
            // account's. The epoch still moves (it fences in-flight request stamps and protocol install), but
            // flipping Offline→Idle on every resident row named those keys in the Session publication and rejoined
            // every leased query after silent login. Leave the entries alone; an empty confirmed set is free.
            if (confirmed)
            {
                keys = [];
            }
            else
            {
                foreach (var entry in _entries.Values)
                {
                    entry.RequestId = 0;
                    var before = entry.Snapshot;
                    var next = before with
                    {
                        Activity = online && ActiveScope(before.Key.Scope) ? ResourceActivity.Idle : ResourceActivity.Offline,
                        Generation = before.Generation + 1, Error = null, RetryAt = null,
                        Knowledge = before.Knowledge == Knowledge.Unsupported
                            ? before.Value is null ? Knowledge.Unknown : Knowledge.Present : before.Knowledge,
                    };
                    entry.Snapshot = next;
                }
                keys = _entries.Keys.ToArray();
            }
        }
        PublishKeys(keys, CatalogChangeKind.Session, confirmed);
        return owner;
    }

    public Task<bool> SetOfflineAsync(long expectedEpoch, CancellationToken ct = default)
        => _commits.ExecuteAsync(_ =>
        {
            bool changed = false;
            _commits.Publish(() => changed = SetOfflineCore(expectedEpoch));
            return ValueTask.FromResult(changed);
        }, ct);

    internal bool SetOfflineCore(long expectedEpoch)
    {
        if (!_commits.IsExecuting) throw new InvalidOperationException("Session changes require the shared commit owner.");
        ResourceKey[] keys;
        lock (_gate)
        {
            if (_epoch != expectedEpoch) return false;
            _online = false; _epoch++;
            keys = _entries.Keys.ToArray();
            foreach (var entry in _entries.Values)
            {
                entry.RequestId = 0;
                entry.Snapshot = entry.Snapshot with { Activity = ResourceActivity.Offline,
                    Generation = entry.Snapshot.Generation + 1, Error = null, RetryAt = null };
            }
        }
        PublishKeys(keys, CatalogChangeKind.Session);
        return true;
    }

    public async Task InvalidateAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct = default)
    {
        await PreloadAsync(keys, ct).ConfigureAwait(false);
        await _commits.CommitAsync(async token =>
        {
            var updates = new Dictionary<ResourceKey, ResourceSnapshot>();
            var records = new List<CatalogRecord>();
            foreach (var key in keys)
            {
                await LoadCoreAsync(key, token).ConfigureAwait(false);
                var old = Peek(key);
                var next = old with { Generation = old.Generation + 1, ExpiresAt = default,
                    Activity = ResourceActivity.Idle, Error = null, RetryAt = null };
                updates[key] = next;
                if (next.Knowledge is Knowledge.Present or Knowledge.Absent) records.Add(ToRecord(next));
            }
            return Commit(updates, records, true);
        }, ct).ConfigureAwait(false);
    }

    public Task SetActivityAsync(ResourceKey key, RequestStamp stamp, ResourceActivity activity,
        ResourceError? error = null, DateTimeOffset? retryAt = null, CancellationToken ct = default)
        => _commits.ExecuteAsync(_ =>
        {
            bool changed = false;
            lock (_gate)
            {
                if (Matches(key, stamp))
                {
                    var entry = EntryOf(key);
                    var next = entry.Snapshot with { Activity = activity, Error = error, RetryAt = retryAt };
                    changed = next != entry.Snapshot;
                    entry.Snapshot = next;
                }
            }
            if (changed) PublishKeys([key], CatalogChangeKind.Activity);
            return ValueTask.FromResult(changed);
        }, ct);

    /// <summary>Restores Idle for a job the coordinator completed as Superseded/Deferred with nothing left
    /// tracking it — a stale-generation supersede (a sibling caller captured the same key directly, racing the
    /// coordinator's own bookkeeping) means the job's OWN stamp can never <see cref="Matches"/> again, so
    /// <see cref="SetActivityAsync"/> is not usable here: this clears purely by key, gated only on the entry
    /// still reading Queued/Fetching (never touches a value/knowledge/error field). The caller (ResourceCoordinator)
    /// is responsible for confirming, under its own lock, that no replacement job has since been admitted.</summary>
    public Task ClearAbandonedActivityAsync(ResourceKey key, CancellationToken ct = default)
        => _commits.ExecuteAsync(_ =>
        {
            bool changed = false;
            lock (_gate)
            {
                var entry = EntryOf(key);
                if (entry.Snapshot.Activity is ResourceActivity.Queued or ResourceActivity.Fetching)
                {
                    entry.Snapshot = entry.Snapshot with { Activity = ResourceActivity.Idle, RetryAt = null };
                    changed = true;
                }
            }
            if (changed) PublishKeys([key], CatalogChangeKind.Activity);
            return ValueTask.FromResult(changed);
        }, ct);

    public Task SetActivitiesAsync(IReadOnlyList<ResourceRequest> requests, ResourceActivity activity,
        CancellationToken ct = default) => _commits.ExecuteAsync(_ =>
        {
            var changed = new List<ResourceKey>();
            lock (_gate)
                foreach (var request in requests)
                {
                    if (!Matches(request.Key, request.Stamp)) continue;
                    var entry = EntryOf(request.Key);
                    var next = entry.Snapshot with { Activity = activity, Error = null, RetryAt = null };
                    if (next == entry.Snapshot) continue;
                    entry.Snapshot = next;
                    changed.Add(request.Key);
                }
            PublishKeys(changed, CatalogChangeKind.Activity);
            return ValueTask.FromResult(true);
        }, ct);

    public async Task<IReadOnlyList<ResourceEnsureResult>> AcceptAsync(IReadOnlyList<ResourceResponse> responses,
        CancellationToken ct = default)
    {
        int bytes = 0;
        var touched = new List<ResourceKey>(responses.Count);
        foreach (var response in responses)
        {
            bytes = checked(bytes + PayloadBytes(response.Result.Patch));
            touched.Add(response.Request.Key);
            if (response.Seeds is not null)
                foreach (var seed in response.Seeds)
                { bytes = checked(bytes + PayloadBytes(seed.Patch)); touched.Add(seed.Key); }
            if (response.Transports is not null)
                foreach (var transport in response.Transports) bytes = checked(bytes + transport.Payload.Length + 256);
        }
        // Cold-load every key this acceptance will reduce against BEFORE taking the writer, so the commit itself
        // performs no I/O and LoadCoreAsync below resolves from memory.
        await PreloadAsync(touched, ct).ConfigureAwait(false);
        var results = await _commits.CommitAsync(async token =>
        {
            var updates = new Dictionary<ResourceKey, ResourceSnapshot>();
            var records = new Dictionary<ResourceKey, CatalogRecord>();
            var transports = new Dictionary<(CatalogScope Scope, string Subject, int Kind), CatalogTransportRecord>();
            var results = new List<ResourceEnsureResult>(responses.Count);
            foreach (var response in responses)
            {
                var request = response.Request;
                var key = request.Key;
                bool valid;
                lock (_gate) valid = Matches(key, request.Stamp) && RequestAllowed(key.Scope, request.RequiresNetwork);
                if (!valid) { results.Add(new(key, ResourceEnsureStatus.Superseded, Peek(key))); continue; }
                await LoadCoreAsync(key, token).ConfigureAwait(false);
                var old = updates.TryGetValue(key, out var staged) ? staged : Peek(key);
                var next = Reduce(old, response.Result);
                updates[key] = next;
                if (response.Result.Status is ResourceFetchStatus.Present or ResourceFetchStatus.Absent or ResourceFetchStatus.NotModified
                    && next.Error is null) records[key] = ToRecord(next);
                results.Add(new(key, StatusOf(next), next));
                if (next.Error is null && response.Transports is not null)
                    foreach (var transport in response.Transports)
                    {
                        if (transport.Scope != key.Scope)
                            throw new ArgumentException("Transport observations must belong to the origin scope.");
                        transports[(transport.Scope, transport.Subject, transport.ExtensionKind)] = transport;
                    }
                if (next.Error is null && response.Result.Status is (ResourceFetchStatus.Present or ResourceFetchStatus.NotModified)
                    && response.Seeds is not null)
                    foreach (var seed in response.Seeds)
                    {
                        if (seed.Key.Scope != (key.Scope with { Provider = seed.Key.Scope.Provider }) || seed.Patch.Facet != seed.Key.Facet)
                            throw new ArgumentException("Child observations must belong to the origin scope and their own facet.");
                        await LoadCoreAsync(seed.Key, token).ConfigureAwait(false);
                        var child = updates.TryGetValue(seed.Key, out var stagedChild) ? stagedChild : Peek(seed.Key);
                        var seeded = ReduceSeed(child, seed.Patch);
                        if (seeded == child) continue;
                        updates[seed.Key] = seeded;
                        records[seed.Key] = ToRecord(seeded);
                    }
            }
            long revision;
            lock (_gate) revision = _revision + 1;
            return Commit(updates, records.Values.ToArray(), (IReadOnlyList<ResourceEnsureResult>)results
                .Select(result => result.Status == ResourceEnsureStatus.Superseded ? result
                    : result with { Snapshot = result.Snapshot with { Revision = revision } }).ToArray(),
                transports.Values.ToArray());
        }, ct, bytes).ConfigureAwait(false);
        return results;
    }

    public async Task SeedManyAsync(IReadOnlyList<CatalogSeed> seeds, long expectedEpoch, CancellationToken ct = default)
    {
        int bytes = CatalogPayloadCodec.MeasureObservations(seeds.Select(seed => new CatalogObservation(seed.Key, seed.Patch, true)).ToArray());
        await PreloadAsync(seeds.Select(seed => seed.Key).ToArray(), ct).ConfigureAwait(false);
        await _commits.CommitAsync(async token =>
        {
            lock (_gate)
                if (_epoch != expectedEpoch || seeds.Any(seed => !ActiveScope(seed.Key.Scope)))
                    throw new OperationCanceledException("Inline observations belong to a previous catalog session.");
            var updates = new Dictionary<ResourceKey, ResourceSnapshot>();
            foreach (var seed in seeds)
            {
                if (seed.Patch.Facet != seed.Key.Facet) throw new ArgumentException("Seed does not own its facet.", nameof(seeds));
                await LoadCoreAsync(seed.Key, token).ConfigureAwait(false);
                var old = updates.TryGetValue(seed.Key, out var staged) ? staged : Peek(seed.Key);
                // Not readiness-gated: unlike a decoder's CHILD reference seed (an ArtistRef, a disc track — a
                // byproduct of decoding something else, never independently confirmed), every seed here is the
                // app's own directly observed data about a subject it is actively playing/queueing — a locally
                // known duration, say, with no resolved display name yet. Gating on the name would silently drop
                // that data (Knowledge stays Unknown ⇒ CatalogReadView.Fact returns null for every field, not
                // just the name) instead of merely declining to invent a fake Present with no label.
                var next = ReduceSeed(old, seed.Patch, gateReadiness: false);
                if (next != old) updates[seed.Key] = next;
            }
            return Commit(updates, updates.Values.Select(ToRecord).ToArray(), true);
        }, ct, bytes).ConfigureAwait(false);
    }

    /// <summary>Accepts a trusted local-owner observation under its captured epoch through the sole durable writer.</summary>
    public async Task<bool> ObserveAsync(IReadOnlyList<CatalogObservation> observations, long expectedEpoch, CancellationToken ct = default)
    {
        await PreloadAsync(observations.Select(observation => observation.Key).ToArray(), ct).ConfigureAwait(false);
        return await _commits.CommitAsync(async token =>
        {
            if (Epoch != expectedEpoch)
                return new DataCommit<bool>(static _ => ValueTask.CompletedTask, static () => { }, false);
            var prepared = await PrepareObservationsAsync(observations, token).ConfigureAwait(false);
            return new DataCommit<bool>(persistToken => _persistence.CommitAsync(prepared.Commit, persistToken), prepared.Publish, true);
        }, ct, CatalogPayloadCodec.MeasureObservations(observations)).ConfigureAwait(false);
    }

    /// <summary>For a replica transaction already executing on this data owner. The caller validates its own
    /// authenticated attempt, persists Commit together with the replica, then invokes Publish exactly once.</summary>
    public async ValueTask<PreparedCatalogObservations> PrepareObservationsAsync(
        IReadOnlyList<CatalogObservation> observations, CancellationToken ct = default)
    {
        if (!_commits.IsExecuting) throw new InvalidOperationException("Catalog observations must be prepared inside the shared data commit owner.");
        // Always the nested case (the caller owns the worker), so this takes PreloadAsync's in-place path; a
        // caller that can preload before its transaction — ObserveAsync does — leaves nothing to read here.
        await PreloadAsync(observations.Select(observation => observation.Key).ToArray(), ct).ConfigureAwait(false);
        var updates = new Dictionary<ResourceKey, ResourceSnapshot>();
        foreach (var observation in observations)
        {
            lock (_gate)
                if (!ActiveScope(observation.Key.Scope)
                    || (!observation.FillUnknownOnly && observation.Key.Scope.Provider == "spotify" && !HasAuthenticatedContext(observation.Key.Scope)))
                    throw new InvalidOperationException("Catalog observation belongs to a different authenticated context.");
            var old = updates.TryGetValue(observation.Key, out var staged) ? staged : Peek(observation.Key);
            if (observation.Patch.Facet != observation.Key.Facet)
                throw new ArgumentException("Observation patch does not own its facet.", nameof(observations));
            var next = observation.FillUnknownOnly ? ReduceSeed(old, observation.Patch)
                : Reduce(old, ResourceFetchResult.Present(observation.Patch));
            if (next.Error is not null) throw new ArgumentException(next.Error.Message, nameof(observations));
            if (observation.FillUnknownOnly && next == old) continue;
            updates[observation.Key] = next with { Generation = old.Generation + (observation.FillUnknownOnly ? 0 : 1) };
        }
        long revision;
        lock (_gate) revision = _revision + 1;
        var records = updates.Values.Select(snapshot => ToRecord(snapshot) with { Revision = revision }).ToArray();
        return new(new CatalogCommit(revision, records), () => PublishUpdates(updates, revision));
    }

    static int PayloadBytes(CatalogPatch? patch) => patch is null ? 0
        : checked(CatalogPayloadCodec.Encode(patch.Apply(null, fillUnknownOnly: true)).Length + 256);

    // gateReadiness: true for a CHILD reference seed — a byproduct of decoding something else (an ArtistRef, a
    // disc track from AlbumTracks, a library-sync header's referenced item), never independently confirmed on
    // its own. false for the app's own directly observed data about a subject it owns (SeedManyAsync's queue/
    // playback tracks) — gating those would silently drop every OTHER field too (Knowledge stays Unknown ⇒
    // CatalogReadView.Fact returns null for the whole value, not just the name), not merely decline a fake label.
    static ResourceSnapshot ReduceSeed(ResourceSnapshot old, CatalogPatch patch, bool gateReadiness = true)
    {
        if (old.Knowledge is Knowledge.Absent or Knowledge.Unsupported) return old;
        // An Unknown entry only promotes to Present when the seed actually carries the field that makes it
        // nameable — a gid-only reference (a LeanAlbum disc track, an ArtistRef) must stay Unknown, so the UI
        // reads "never asked" (not "seeded but nameless") and the normal fetch still proceeds (IsFresh already
        // requires Provider provenance). A seed that ADDS fields to an already-Present entry always applies.
        if (gateReadiness && old.Knowledge == Knowledge.Unknown && !HasReadinessField(patch)) return old;
        var value = CatalogValueRules.Normalize(patch.Apply(old.Value, fillUnknownOnly: true), old.Value);
        CatalogValueRules.Validate(value);
        return old with { Knowledge = Knowledge.Present, Value = value,
            Provenance = old.Knowledge == Knowledge.Present ? old.Provenance : CatalogProvenance.InlineSeed };
    }

    /// <summary>The one field per facet that makes an entity nameable — TrackIdentity/EpisodeIdentity: Title;
    /// ArtistIdentity/AlbumIdentity/ShowIdentity/PlaylistHeader: Name. Every other patch (relations, availability,
    /// play count, …) always counts — it never represents a partial identity guess, only a complete answer.</summary>
    static bool HasReadinessField(CatalogPatch patch) => patch switch
    {
        TrackIdentityPatch p => IsNamed(p.Title),
        EpisodeIdentityPatch p => IsNamed(p.Title),
        ArtistIdentityPatch p => IsNamed(p.Name),
        AlbumIdentityPatch p => IsNamed(p.Name),
        ShowIdentityPatch p => IsNamed(p.Name),
        PlaylistHeaderPatch p => IsNamed(p.Name),
        _ => true,
    };
    static bool IsNamed(FieldChange<string?> field) => field.IsSpecified && !string.IsNullOrEmpty(field.Value);

    ResourceSnapshot Reduce(ResourceSnapshot current, ResourceFetchResult result)
    {
        var next = current with { Activity = ResourceActivity.Idle, Error = null, RetryAt = null };
        var now = _time.GetUtcNow();
        switch (result.Status)
        {
            case ResourceFetchStatus.Present:
                if (result.Patch is null || result.Patch.Facet != current.Key.Facet)
                    return Invalid(next, "Response does not own the requested facet.");
                var value = CatalogValueRules.Normalize(result.Patch.Apply(current.Value), current.Value);
                CatalogValueRules.Validate(value);
                return next with { Knowledge = Knowledge.Present, Value = value,
                    Provenance = CatalogProvenance.Provider, FetchedAt = now,
                    ExpiresAt = ResourcePolicy.ExpiresAt(value, now, result.FreshFor) };
            case ResourceFetchStatus.NotModified:
                if (current.Knowledge != Knowledge.Present || current.Value is null
                    || current.Provenance != CatalogProvenance.Provider)
                    return Invalid(next, "Not-modified response has no matching provider value.");
                return next with { FetchedAt = now,
                    ExpiresAt = ResourcePolicy.ExpiresAt(current.Value, now, result.FreshFor) };
            case ResourceFetchStatus.Absent:
                return next with { Knowledge = Knowledge.Absent, Value = null,
                    Provenance = CatalogProvenance.Provider, FetchedAt = now,
                    ExpiresAt = now + ResourcePolicy.FreshFor(current.Key.Facet, result.FreshFor, absent: true) };
            case ResourceFetchStatus.Unsupported:
                return next with { Knowledge = Knowledge.Unsupported };
            default:
                return next with { Error = result.Error ?? new ResourceError(ResourceErrorKind.Transport, "Provider request failed.") };
        }
    }

    static ResourceSnapshot Invalid(ResourceSnapshot snapshot, string message)
        => snapshot with { Error = new ResourceError(ResourceErrorKind.InvalidResponse, message) };

    static ResourceEnsureStatus StatusOf(ResourceSnapshot snapshot) => snapshot.Error is not null ? ResourceEnsureStatus.Failed
        : snapshot.Knowledge switch
        {
            Knowledge.Present => ResourceEnsureStatus.Ready,
            Knowledge.Absent => ResourceEnsureStatus.Absent,
            Knowledge.Unsupported => ResourceEnsureStatus.Unsupported,
            _ => ResourceEnsureStatus.Deferred,
        };

    /// <summary>Cold-reads every not-yet-loaded key OUTSIDE the commit worker in one persistence round trip, then
    /// installs the results in one short command. I/O is not queue work: a cold cache used to run one key at a
    /// time on the single writer, so a page of keys delayed every accept, session change and replica adopt behind
    /// it. Two concurrent preloads of one key are safe (the second install no-ops) and an accept that lands first
    /// wins — a passive cold value only ever fills an entry that is still Unknown.</summary>
    async Task PreloadAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return;
        // Already ON the worker (PrepareObservationsAsync inside a replica transaction, or a command's own
        // LoadCoreAsync): a nested ExecuteAsync would deadlock the single reader, so load in place.
        if (_commits.IsExecuting)
        {
            foreach (var key in keys) await LoadCoreAsync(key, ct).ConfigureAwait(false);
            return;
        }
        var pending = new List<ResourceKey>();
        var seen = new HashSet<ResourceKey>();
        lock (_gate)
            foreach (var key in keys)
                if (seen.Add(key) && !EntryOf(key).Loaded) pending.Add(key);
        if (pending.Count == 0)
        {
            Volatile.Write(ref _lastColdReadMs, 0);
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var loaded = await _persistence.ReadManyAsync(pending, ct).ConfigureAwait(false);
        Volatile.Write(ref _lastColdReadMs, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (loaded.Count != pending.Count)
            throw new InvalidOperationException("Persistence answered a different number of catalog resources.");
        await _commits.ExecuteAsync(_ =>
        {
            var changed = new List<ResourceKey>();
            lock (_gate)
                for (int i = 0; i < pending.Count; i++)
                {
                    var key = pending[i];
                    var record = loaded[i];
                    if (record is not null && record.Key != key)
                        throw new InvalidOperationException("Persistence returned a different catalog resource.");
                    var entry = EntryOf(key);
                    if (Install(entry, key, record)) changed.Add(key);
                    entry.Loaded = true;
                }
            PublishKeys(changed, CatalogChangeKind.ColdRead);
            return ValueTask.FromResult(true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The in-command fallback: one key, already on the commit worker.</summary>
    async ValueTask LoadCoreAsync(ResourceKey key, CancellationToken ct)
    {
        lock (_gate) if (EntryOf(key).Loaded) return;
        var loaded = (await _persistence.ReadManyAsync([key], ct).ConfigureAwait(false))[0];
        if (loaded is not null && loaded.Key != key)
            throw new InvalidOperationException("Persistence returned a different catalog resource.");
        bool changed;
        lock (_gate)
        {
            var entry = EntryOf(key);
            changed = Install(entry, key, loaded);
            entry.Loaded = true;
        }
        if (changed) PublishKeys([key], CatalogChangeKind.ColdRead);
    }

    /// <summary>Caller holds <see cref="_gate"/>. All accepted values cross the commit queue: a passive cold read
    /// never replaces an already accepted answer.</summary>
    bool Install(Entry entry, ResourceKey key, CatalogRecord? loaded)
    {
        if (entry.Loaded || loaded is null || entry.Snapshot.Knowledge != Knowledge.Unknown) return false;
        var old = entry.Snapshot;
        entry.Snapshot = new(key, loaded.Knowledge, loaded.Value, loaded.Provenance, old.Activity,
            old.Error, loaded.FetchedAt, loaded.ExpiresAt, loaded.Revision, old.Generation, old.RetryAt);
        _revision = Math.Max(_revision, loaded.Revision);
        UpdateSize(entry);
        return true;
    }

    DataCommit<T> Commit<T>(Dictionary<ResourceKey, ResourceSnapshot> updates,
        IReadOnlyList<CatalogRecord> records, T result, IReadOnlyList<CatalogTransportRecord>? transports = null)
    {
        long revision;
        lock (_gate) revision = _revision + 1;
        var persisted = new CatalogRecord[records.Count];
        for (int i = 0; i < records.Count; i++) persisted[i] = records[i] with { Revision = revision };
        return new DataCommit<T>(token => persisted.Length == 0 && transports is not { Count: > 0 } ? ValueTask.CompletedTask
                : _persistence.CommitAsync(new CatalogCommit(revision, persisted, transports), token),
            () => PublishUpdates(updates, revision, persisted.Length == 0 ? CatalogChangeKind.Activity : CatalogChangeKind.Durable), result);
    }

    void PublishUpdates(Dictionary<ResourceKey, ResourceSnapshot> updates, long revision, CatalogChangeKind kind = CatalogChangeKind.Durable)
    {
        lock (_gate)
            foreach (var (key, snapshot) in updates)
            {
                var entry = EntryOf(key);
                entry.Snapshot = snapshot with { Revision = revision };
                UpdateSize(entry);
                entry.Loaded = true;
            }
        if (updates.Count > 0) PublishKeys(updates.Keys.ToArray(), kind);
    }

    static CatalogRecord ToRecord(ResourceSnapshot snapshot) => new(snapshot.Key, snapshot.Knowledge,
        snapshot.Value, snapshot.Provenance, snapshot.FetchedAt, snapshot.ExpiresAt, snapshot.Revision);

    Entry EntryOf(ResourceKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry = new Entry(ResourceSnapshot.Unknown(key));
            _residentBytes += entry.EstimatedBytes;
            _unknown.Remove(key);
        }
        return entry;
    }

    void UpdateSize(Entry entry)
    {
        long size = CatalogResidentSize.Estimate(entry.Snapshot.Key, entry.Snapshot.Value);
        _residentBytes += size - entry.EstimatedBytes;
        entry.EstimatedBytes = size;
    }

    // Local facts use the current storage/context even before Spotify authentication. Matching this scope does
    // not grant transport access or certify an account-dependent provider answer.
    bool ActiveScope(CatalogScope scope) => scope.ProviderAccount == _actualAccount
        && scope == (_scope with { Provider = scope.Provider });

    bool HasAuthenticatedContext(CatalogScope scope)
        => scope.ContextKnown && _actualAccount.Length > 0 && scope.ProviderAccount == _actualAccount;

    bool RequestAllowed(CatalogScope scope, bool requiresNetwork)
        => (scope.Provider != "spotify" || HasAuthenticatedContext(scope)) && (!requiresNetwork || _online);

    bool Matches(ResourceKey key, RequestStamp stamp) => ActiveScope(key.Scope) && stamp.Scope == key.Scope
        && stamp.ActualAccount == _actualAccount && stamp.ActualAccount == key.Scope.ProviderAccount
        && stamp.Epoch == _epoch && EntryOf(key).Snapshot.Generation == stamp.Generation
        && EntryOf(key).RequestId == stamp.RequestId;

    void PublishKeys(IReadOnlyList<ResourceKey> keys, CatalogChangeKind kind = CatalogChangeKind.Durable,
        bool confirmed = false)
    {
        if (keys.Count == 0 && kind != CatalogChangeKind.Session) return;
        long revision;
        lock (_gate) { revision = ++_revision; RepublishRenderView(keys); }
        _commits.NotifyAfterPublish(() => _changes.Publish(new CatalogChangeSet(revision, keys, kind, confirmed)));
    }

    /// <summary>Caller holds <see cref="_gate"/>. Copies the just-published keys into the lock-free view. Only the
    /// changed keys are touched (an untouched entry keeps its published snapshot), and a key whose entry is gone —
    /// a trim or an eviction — leaves the view with it, so the view never resurrects forgotten state.</summary>
    void RepublishRenderView(IReadOnlyList<ResourceKey> keys)
    {
        ImmutableDictionary<ResourceKey, ResourceSnapshot>.Builder? builder = null;
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (!RenderFacet(key.Facet)) continue;
            builder ??= _renderView.ToBuilder();
            if (_entries.TryGetValue(key, out var entry)) builder[key] = entry.Snapshot;
            else builder.Remove(key);
        }
        if (builder is not null) Volatile.Write(ref _renderView, builder.ToImmutable());
    }

    // A bad observer must not turn a committed transaction into an apparent persistence failure.
    sealed class CatalogChanges : IObservable<CatalogChangeSet>
    {
        readonly object _gate = new();
        readonly List<IObserver<CatalogChangeSet>> _observers = new();
        public IDisposable Subscribe(IObserver<CatalogChangeSet> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_gate) _observers.Add(observer);
            return new Subscription(this, observer);
        }
        public void Publish(CatalogChangeSet change)
        {
            IObserver<CatalogChangeSet>[] observers;
            lock (_gate) observers = _observers.ToArray();
            foreach (var observer in observers)
                try { observer.OnNext(change); } catch (Exception) { }
        }
        sealed class Subscription(CatalogChanges owner, IObserver<CatalogChangeSet> observer) : IDisposable
        {
            public void Dispose() { lock (owner._gate) owner._observers.Remove(observer); }
        }
    }
}
