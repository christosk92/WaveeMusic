using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Sync;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Persistence;

/// <summary>Explicit ephemeral storage for the demo backend. It follows the same atomic commit contract as SQLite.</summary>
public sealed partial class MemoryDataPersistence : ICatalogPersistence, IReplicaPersistence
{
    readonly object _gate = new();
    readonly Dictionary<ResourceKey, CatalogRecord> _catalog = new();
    readonly Dictionary<(CatalogScope Scope, string Subject, int Kind), CatalogTransportRecord> _transport = new();
    readonly Dictionary<(string Storage, string Owner), ReplicaBootstrap> _replicas = new();
    long _lastIntentId;
    public ValueTask<IReadOnlyList<CatalogRecord?>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ct.ThrowIfCancellationRequested();
        var records = new CatalogRecord?[keys.Count];
        lock (_gate) for (int i = 0; i < keys.Count; i++) records[i] = _catalog.GetValueOrDefault(keys[i]);
        return ValueTask.FromResult<IReadOnlyList<CatalogRecord?>>(records);
    }
    public ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject, int extensionKind, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_transport.GetValueOrDefault((scope, subject, extensionKind))); }
    public ValueTask<ReplicaBootstrap> LoadAsync(ReplicaScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var state = string.IsNullOrEmpty(scope.Account) ? ReplicaBootstrap.Empty
                : _replicas.GetValueOrDefault((scope.StorageAccount, scope.Account), ReplicaBootstrap.Empty);
            return ValueTask.FromResult(state with { Playlists = [], LastIntentId = _lastIntentId });
        }
    }
    public ValueTask<PlaylistReplicaBaseline?> LoadPlaylistAsync(ReplicaScope scope, string uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (string.IsNullOrEmpty(scope.Account) || !_replicas.TryGetValue((scope.StorageAccount, scope.Account), out var state))
                return ValueTask.FromResult<PlaylistReplicaBaseline?>(null);
            foreach (var row in state.Playlists) if (row.Uri == uri) return ValueTask.FromResult<PlaylistReplicaBaseline?>(row);
            return ValueTask.FromResult<PlaylistReplicaBaseline?>(null);
        }
    }
    public ValueTask CommitAsync(CatalogCommit commit, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (_gate) Apply(commit); return ValueTask.CompletedTask; }
    void Apply(CatalogCommit commit)
    {
        foreach (var record in commit.Records) _catalog[record.Key] = record;
        if (commit.Transports is not null)
            foreach (var record in commit.Transports) _transport[(record.Scope, record.Subject, record.ExtensionKind)] = record;
    }
    public ValueTask CommitAsync(ReplicaTransaction transaction, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            bool hasReplicaWrites = !transaction.Playlists.IsEmpty || transaction.Rootlist is not null || !transaction.Collections.IsEmpty
                || !transaction.SaveIntents.IsEmpty || !transaction.RemoveIntents.IsEmpty;
            if (hasReplicaWrites && string.IsNullOrEmpty(transaction.Scope.Account))
                throw new InvalidOperationException("Replica writes require an authenticated owner.");
            var key = (transaction.Scope.StorageAccount, transaction.Scope.Account ?? "");
            var prior = _replicas.GetValueOrDefault(key, ReplicaBootstrap.Empty);
            var playlists = prior.Playlists.ToBuilder();
            foreach (var row in transaction.Playlists)
            {
                if (row.State == ReplicaBaselineState.RecoveryOnly) throw new InvalidOperationException("Recovery evidence is not a confirmed baseline.");
                var normalized = row with { Header = null };
                int index = FindIndex(playlists, p => p.Uri == row.Uri);
                if (index >= 0) playlists[index] = normalized; else playlists.Add(normalized);
            }
            var collections = prior.Collections.ToBuilder();
            foreach (var row in transaction.Collections)
            {
                int index = FindIndex(collections, c => c.SetId == row.SetId);
                if (index >= 0) collections[index] = row; else collections.Add(row);
            }
            var intents = prior.Intents.ToBuilder();
            foreach (var id in transaction.RemoveIntents)
            { int index = FindIndex(intents, i => i.Id == id); if (index >= 0) intents.RemoveAt(index); }
            foreach (var row in transaction.SaveIntents)
            {
                if (row.OwnerAccount != transaction.Scope.Account || row.StorageAccount != transaction.Scope.StorageAccount)
                    throw new InvalidOperationException("An intent cannot be sent under a different storage or provider account.");
                foreach (var pair in _replicas)
                    if (pair.Key != key && FindIndex(pair.Value.Intents.ToBuilder(), i => i.Id == row.Id) >= 0)
                        throw new InvalidOperationException("This intent ID already belongs to another account.");
                int index = FindIndex(intents, i => i.Id == row.Id);
                if (index >= 0) intents[index] = row; else intents.Add(row);
                _lastIntentId = Math.Max(_lastIntentId, row.Id);
            }
            var next = new ReplicaBootstrap(playlists.ToImmutable(), transaction.Rootlist ?? prior.Rootlist,
                collections.ToImmutable(), intents.ToImmutable()) { LastIntentId = _lastIntentId };
            if (transaction.Catalog is { } catalog) Apply(catalog);
            _replicas[key] = next;
        }
        return ValueTask.CompletedTask;
    }
    static int FindIndex<T>(ImmutableArray<T>.Builder rows, Func<T, bool> predicate)
    {
        for (int i = 0; i < rows.Count; i++) if (predicate(rows[i])) return i;
        return -1;
    }
}
