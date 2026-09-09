using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Sync;
using Wavee.Backend.Catalog;

namespace Wavee.Backend.Queries;

/// <summary>Routes query demand into the existing session protocol lane. Offline reads retain cached state.</summary>
public sealed class LibraryQueryDemand(Func<LibrarySync?> session, LibraryReplicaCoordinator replicas,
    ProviderExecutionGate? execution = null, Func<long>? epoch = null) : IQueryReplicaDemand
{
    public async Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
    {
        if (requests.Count == 0) return [];
        long expectedEpoch = epoch?.Invoke() ?? 0;
        // The playlist-cache warm needs no sync — it reads whatever the replica coordinator already knows — so it
        // always runs, protocol session or not.
        foreach (var request in requests.Where(x => x.Kind == "playlist").Distinct())
            await replicas.EnsurePlaylistCachedAsync(request.Id, ct).ConfigureAwait(false);
        if (execution is not null && !await execution.WaitAsync(expectedEpoch, ct).ConfigureAwait(false)) return requests;
        var sync = session();
        if (sync is null)
        {
            // A local/demo runtime may intentionally have no protocol. A live session can also retire after
            // admission. Neither case consumes a remote request; the caller receives the unserved requests.
            var deferred = requests.Distinct().ToArray();
            if (deferred.Length > 0)
                WaveeLog.Instance.Event(WaveeLogLevel.Info, "catalog", "replica.demand.deferred",
                    "Replica demand deferred — no protocol session installed",
                    fields:
                    [
                        WaveeLogField.Of("count", deferred.Length),
                        WaveeLogField.Of("first", deferred[0].Kind + ":" + deferred[0].Id),
                    ]);
            return deferred;
        }
        await Task.WhenAll(requests.Distinct().Select(request => request.Kind switch
        {
            "playlist" => sync.EnsurePlaylistAsync(request.Id, force, ct),
            "collection" => sync.EnsureCollectionAsync(request.Id, force, ct),
            "rootlist" => sync.EnsureRootlistAsync(force, ct),
            _ => Task.FromException(new ArgumentException("Unknown replica demand kind: " + request.Kind)),
        })).ConfigureAwait(false);
        return [];
    }
}
