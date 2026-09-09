using System.Linq;
using Wavee.Backend.Sync;

namespace Wavee.Backend.Persistence;

/// <summary>Effective membership projection for command/UI compatibility. Metadata is owned exclusively by catalog facets.</summary>
public sealed class MemoryReplicaProjection(InMemoryStore store) : IReplicaProjectionSink
{
    public void Publish(ReplicaProjection projection)
    {
        using var bulk = store.BeginBulk();
        foreach (var playlist in projection.Playlists)
            store.SetMembership(playlist.Uri, playlist.Members, playlist.Revision);
        if (projection.Rootlist is { } root) store.SetRootlist(root.Entries, root.Revision);
        foreach (var collection in projection.Collections)
        {
            var present = collection.Items.Select(row => row.Uri).ToHashSet(System.StringComparer.Ordinal);
            foreach (var uri in store.SavedUris(collection.SetId).ToArray())
                if (!present.Contains(uri)) store.SetSaved(collection.SetId, uri, false,
                    collection.PendingUris.Contains(uri) ? SyncState.Pending : SyncState.Confirmed);
            foreach (var row in collection.Items)
                store.SetSaved(collection.SetId, row.Uri, true, collection.PendingUris.Contains(row.Uri) ? SyncState.Pending : SyncState.Confirmed, row.AddedAtMs);
        }
    }
}
