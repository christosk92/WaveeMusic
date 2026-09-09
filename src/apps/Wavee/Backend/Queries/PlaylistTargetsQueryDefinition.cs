using System;
using System.Linq;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Queries;

/// <summary>Interactive target selection needs permissions across rootlist membership, never playlist contents.</summary>
public sealed class PlaylistTargetsQueryDefinition(PlaylistTargetsQuery query, LibraryReplicaCoordinator replicas,
    Func<string, string> ownerProvider) : IQueryDefinition<PlaylistTargetsSnapshot>
{
    public QueryReadResult<PlaylistTargetsSnapshot> Pending => new(PlaylistTargetsSnapshot.Empty, 0, false);
    readonly CatalogReadView _view = new(query.Scope, replicas, ownerProvider);

    public QueryReadResult<PlaylistTargetsSnapshot> Read(QueryReadContext read)
    {
        read.DependOnReplica("rootlist");
        var rootlist = replicas.ReadRootlist();
        var playlists = rootlist.Entries.Where(entry => entry.Kind == 0
                && EntityUri.Parse(entry.Uri) is { IsSpotify: true, Kind: EntityKind.Playlist })
            .DistinctBy(entry => entry.Uri).Select(entry => _view.Playlist(read, entry.Uri)).ToArray();
        bool known = rootlist.State is ReplicaBaselineState.Cached or ReplicaBaselineState.Verified;
        return new(new(playlists, known), rootlist.Version, known);
    }

    public QueryRequirements Requirements(PlaylistTargetsSnapshot value, QueryDemand demand)
        => new(value.Playlists.Select(playlist => _view.Key(playlist.Uri, FacetKind.PlaylistHeader)).ToArray(),
            [new("rootlist", "rootlist")]);
}
