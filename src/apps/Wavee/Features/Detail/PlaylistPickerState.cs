using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Unknown permissions are unresolved target discovery, not evidence that a playlist is read-only.</summary>
internal sealed record PlaylistPickerState(IReadOnlyList<PlaylistSummary> Items, bool HasUnresolved, bool Unavailable)
{
    public static PlaylistPickerState Read(QuerySnapshot<PlaylistTargetsSnapshot>? snapshot,
        IReadOnlyList<string>? recentUris, string? excludeUri, string? filter)
    {
        if (snapshot is null) return new([], true, false);
        bool unresolved = !snapshot.Value.MembershipKnown;
        bool unavailable = snapshot.Failure is not null;
        var completedHeaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in snapshot.Resources.Values)
            if (resource.Key.Facet == FacetKind.PlaylistHeader && resource.Provenance == CatalogProvenance.Provider
                && resource.FetchedAt != default && resource.Activity is ResourceActivity.Idle or ResourceActivity.Backoff)
                completedHeaders.Add(resource.Key.Subject);
        var known = new List<PlaylistSummary>();
        foreach (var playlist in snapshot.Value.Playlists)
        {
            if (string.Equals(playlist.Uri, excludeUri, StringComparison.Ordinal)) continue;
            if (!playlist.Capabilities.Known || playlist.Name.Length == 0)
            {
                unresolved = true;
                unavailable |= completedHeaders.Contains(playlist.Uri);
                continue;
            }
            known.Add(new(playlist.Uri, playlist.Name, playlist.OwnerName, playlist.TrackCount, playlist.Cover,
                CanEdit: playlist.Capabilities.CanEditItems, IsOwner: playlist.Capabilities.IsOwner));
        }
        return new(PlaylistDepositTargets.Order(known, recentUris, excludeUri, filter), unresolved,
            unavailable || unresolved && (snapshot.Status.IsOffline || snapshot.Problems.Count > 0));
    }
}
