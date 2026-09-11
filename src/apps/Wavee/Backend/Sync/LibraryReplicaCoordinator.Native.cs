using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;

namespace Wavee.Backend.Sync;

public sealed partial class LibraryReplicaCoordinator
{
    sealed record NativeState(ImmutableDictionary<string, NativeReplicaContribution> Contributions,
        ImmutableDictionary<string, PlaylistReplicaBaseline> Playlists,
        ImmutableArray<RootlistEntry> Rootlist,
        ImmutableDictionary<string, CollectionReplicaBaseline> Collections, long Version)
    {
        public static NativeState Empty { get; } = new(
            ImmutableDictionary.Create<string, NativeReplicaContribution>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, PlaylistReplicaBaseline>(StringComparer.Ordinal), [],
            ImmutableDictionary.Create<string, CollectionReplicaBaseline>(StringComparer.Ordinal), 0);
    }

    public Task AdoptNativeAsync(NativeReplicaContribution contribution, ReplicaScope expectedScope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contribution.SourceId);
        var bytes = contribution.Playlists.Sum(x => checked(ReplicaPayload.Members(x.Members) + ReplicaPayload.Header(x.Header)))
            + contribution.Collections.Sum(x => x.Items.Sum(row => checked(64 + row.Uri.Length * 2)))
            + (contribution.Rootlist is { } root ? ReplicaPayload.Rootlist(new RootlistReadResult(root.Entries, null)) : 0)
            + CatalogPayloadCodec.MeasureObservations(contribution.Headers);
        // Only occurrence and membership facts remain in the replica. Header/child observations are owned by catalog.
        var retainedContribution = contribution with
        {
            Playlists = contribution.Playlists.Select(playlist => playlist with { Header = null, Revision = null }).ToImmutableArray(),
            Headers = [],
        };
        return _commits.CommitAsync(async token =>
        {
            if (expectedScope != _scope() || expectedScope != _state.Scope)
                throw new OperationCanceledException("The library source belongs to a previous session.");
            var before = _state;
            var contributions = before.Native.Contributions.SetItem(contribution.SourceId, retainedContribution);
            var native = ReduceNative(before, contributions);
            var next = before with { Native = native };
            var playlistKeys = before.Native.Playlists.Keys.Concat(native.Playlists.Keys).Distinct().ToArray();
            foreach (var uri in before.Native.Playlists.Keys.Where(uri => !native.Playlists.ContainsKey(uri)))
            {
                var restored = before.EffectivePlaylists.TryGetValue(uri, out var cached) ? cached : Baseline(before, uri);
                var previous = EffectivePlaylist(before, uri);
                next = next with { EffectivePlaylists = next.EffectivePlaylists.SetItem(uri, restored with
                { OrderRevision = previous.OrderRevision + (SameOrder(previous.Members, restored.Members) ? 0 : 1) }) };
            }
            var sets = before.Native.Collections.Keys.Concat(native.Collections.Keys).Distinct().ToArray();
            var catalog = await _catalog.PrepareObservationsAsync(contribution.Headers, token).ConfigureAwait(false);
            var transaction = new ReplicaTransaction(expectedScope, [], null, [], [], [], [], [], catalog.Commit);
            return new DataCommit<int>(persistToken => _persistence.CommitAsync(transaction, persistToken), () =>
            {
                Volatile.Write(ref _state, next);
                if (expectedScope != _scope()) return;
                catalog.Publish();
                PublishProjection(new ReplicaProjection(playlistKeys.Select(uri => EffectivePlaylist(next, uri)).ToImmutableArray(),
                    ProjectRootlist(next, expectedScope), sets.Select(set => ProjectCollection(next, set, expectedScope)).ToImmutableArray()));
                foreach (var uri in playlistKeys)
                {
                    var previous = EffectivePlaylist(before, uri);
                    var current = EffectivePlaylist(next, uri);
                    NotifyChange(new ReplicaChange(uri, current.Version, !SameOrder(previous.Members, current.Members), false));
                }
                NotifyChange(new ReplicaChange("rootlist", native.Version, true, false));
                foreach (var set in sets) NotifyChange(new ReplicaChange(set, native.Version, true, false));
            }, 0);
        }, ct, encodedBytes: bytes);
    }

    public Task RemoveNativeAsync(string sourceId, ReplicaScope expectedScope, CancellationToken ct = default)
        => AdoptNativeAsync(new NativeReplicaContribution(sourceId, int.MaxValue, [], null, [], []), expectedScope, ct);

    static PlaylistReplicaBaseline EffectivePlaylist(State state, string uri)
        => state.Native.Playlists.TryGetValue(uri, out var native) ? native
            : state.EffectivePlaylists.TryGetValue(uri, out var effective) ? effective : Baseline(state, uri);

    static RootlistReplicaBaseline MergeNativeRootlist(State state, RootlistReplicaBaseline protocol)
    {
        if (state.Native.Rootlist.IsEmpty) return protocol with { Version = protocol.Version + state.Native.Version };
        var rows = protocol.Entries.ToBuilder();
        var present = rows.Where(x => x.Kind == 0).Select(x => x.Uri).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in state.Native.Rootlist)
            if (entry.Kind != 0 || present.Add(entry.Uri)) rows.Add(entry with { Position = rows.Count });
        return protocol with { Entries = rows.ToImmutable(), Version = protocol.Version + state.Native.Version };
    }

    static NativeState ReduceNative(State state, ImmutableDictionary<string, NativeReplicaContribution> contributions)
    {
        var playlists = ImmutableDictionary.CreateBuilder<string, PlaylistReplicaBaseline>(StringComparer.Ordinal);
        var roots = ImmutableArray.CreateBuilder<RootlistEntry>();
        var rootUris = new HashSet<string>(StringComparer.Ordinal);
        var collections = new Dictionary<string, Dictionary<string, SavedItem>>(StringComparer.Ordinal);
        var version = state.Native.Version + 1;
        foreach (var source in contributions.Values.OrderBy(x => x.Priority).ThenBy(x => x.SourceId, StringComparer.Ordinal))
        {
            foreach (var playlist in source.Playlists)
                if (!playlists.ContainsKey(playlist.Uri))
                {
                    var previous = EffectivePlaylist(state, playlist.Uri);
                    playlists.Add(playlist.Uri, playlist with { Revision = null, State = ReplicaBaselineState.Verified,
                        Version = version, OrderRevision = previous.OrderRevision + (SameOrder(previous.Members, playlist.Members) ? 0 : 1) });
                }
            if (source.Rootlist is { } root)
                foreach (var entry in root.Entries)
                    if (entry.Kind != 0 || rootUris.Add(entry.Uri)) roots.Add(entry with { Position = roots.Count });
            foreach (var set in source.Collections)
            {
                if (!collections.TryGetValue(set.SetId, out var items)) collections.Add(set.SetId, items = new(StringComparer.Ordinal));
                foreach (var item in set.Items) items.TryAdd(item.Uri, item);
            }
        }
        return new NativeState(contributions, playlists.ToImmutable(), roots.ToImmutable(),
            collections.ToImmutableDictionary(x => x.Key, x => new CollectionReplicaBaseline(x.Key, x.Value.Values.ToImmutableArray(), Version: version),
                StringComparer.Ordinal), version);
    }
}
