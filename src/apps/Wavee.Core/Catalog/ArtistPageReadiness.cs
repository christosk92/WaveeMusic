using System.Collections.Generic;

namespace Wavee.Core.Catalog;

/// <summary>
/// The artist page's own initial-load boundary. <c>ArtistDetailQuery</c> publishes as soon as the identity facet is
/// known (a library-cached name and avatar), long before the overview (hero image, biography, listener counts), the
/// popular chart membership, the discography rows and their per-item identities land. Releasing the whole-page
/// skeleton on that first partial value re-laid the hero, swapped its artwork and left every row on its own
/// placeholder. The shared rule (<see cref="PageReadiness"/>) holds the pending shape until each PRIMARY fact the
/// initial demand asked for has a terminal answer; this page additionally requires its identity and overview facts
/// to be among them, so a read that has not named them yet is never a load.
///
/// Discography / appears-on / related relation pages and per-item identities (including popular track
/// identities) are still demanded (the page asks for its whole model) but they do not hold the skeleton:
/// those cards and chart rows paint as loading rows the same way a playlist paints unknown members.
/// </summary>
public static class ArtistPageReadiness
{
    public static bool IsResolved(ResourceSnapshot resource) => PageReadiness.IsResolved(resource);

    public static bool InitialLoadComplete(QuerySnapshot<Artist> snapshot)
        => InitialLoadComplete(snapshot, PageReadiness.PresentationFacets);

    public static bool InitialLoadComplete(QuerySnapshot<Artist> snapshot, IReadOnlyList<FacetKind> optionalFacets)
    {
        if (snapshot.Revision == 0) return false;
        if (snapshot.Status.Superseded) return false;
        if (snapshot.Demanded.Count == 0) return false;
        string uri = snapshot.Value.Uri;
        bool identity = false, overview = false;
        foreach (var key in snapshot.Demanded)
        {
            if (key.Subject == uri)
            {
                identity |= key.Facet == FacetKind.ArtistIdentity;
                overview |= key.Facet == FacetKind.ArtistOverview;
            }
            if (PageReadiness.IsOptional(key.Facet, optionalFacets)) continue;
            if (IsBelowFoldIdentity(key, uri)) continue;
            if (!snapshot.Resources.TryGetValue(key, out var resource) || !PageReadiness.IsResolved(resource))
                return false;
        }
        return identity && overview;
    }

    /// <summary>Below-fold and per-row facts. The artist's own identity, overview and popular-membership
    /// facets stay on the critical path so the hero and chart exist; discography / related relation pages and every
    /// other subject's identity paint as loading rows instead of holding the whole-page skeleton.</summary>
    internal static bool IsBelowFoldIdentity(ResourceKey key, string artistUri)
    {
        if (key.Subject != artistUri)
            return key.Facet is FacetKind.AlbumIdentity or FacetKind.ArtistIdentity or FacetKind.PlaylistHeader
                or FacetKind.TrackIdentity or FacetKind.EpisodeIdentity;
        return key.Facet is FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn or FacetKind.ArtistRelated;
    }
}
