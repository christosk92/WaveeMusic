using System.Collections.Generic;

namespace Wavee.Core.Catalog;

/// <summary>
/// The shared page-owned initial-load boundary: a query page keeps its skeleton until every fact it is asking for
/// has a terminal answer, then reveals once; later publications are background data on a Ready page. Per-field
/// presentation facets (play counts, audio attributes, video flags, descriptors, availability) render as their own
/// field states and never hold a page. Engine-free and pure; each page decides whether to opt in.
/// </summary>
public static class PageReadiness
{
    /// <summary>Facets a row paints as a field state of its own ("0 plays", pending, unavailable, offline).</summary>
    public static IReadOnlyList<FacetKind> PresentationFacets { get; } =
        [FacetKind.PlayCount, FacetKind.Descriptors, FacetKind.AudioAttributes, FacetKind.Availability,
         FacetKind.VideoAssociation, FacetKind.Publishing, FacetKind.VisualIdentity];

    /// <summary>A resource is resolved once the catalog has a terminal answer: data, a negative, unsupported, a
    /// failure, or offline under a known catalog context. Unknown with idle/queued/fetching/backoff activity is still
    /// in flight, and so is "offline" under the pre-login scope: at a cold start the session has simply not installed
    /// yet, and the same keys are re-read under the real scope moments later.</summary>
    public static bool IsResolved(ResourceSnapshot resource)
        => resource.Knowledge != Knowledge.Unknown || resource.Error is not null
            || (resource.Activity == ResourceActivity.Offline && resource.Key.Scope.ContextKnown);

    /// <summary>Every key the query is demanding, except <paramref name="optionalFacets"/>, has a terminal answer.
    /// Only what the query is asking for can hold the page: <see cref="QuerySnapshot{T}.Resources"/> also lists keys
    /// merely READ while joining the value (a related artist's identity outside any demanded window), and nothing
    /// fetches those until demand reaches them, so waiting on them would keep the skeleton up forever.</summary>
    public static bool DemandedResolved<T>(QuerySnapshot<T> snapshot, IReadOnlyList<FacetKind> optionalFacets)
    {
        if (snapshot.Revision == 0) return false;   // the acquisition seed, not a read
        if (snapshot.Status.Superseded) return false;   // read under a scope the session moved away from; a re-acquire is in flight
        if (snapshot.Demanded.Count == 0) return false;   // nothing asked for yet (inactive lease or pre-read)
        foreach (var key in snapshot.Demanded)
        {
            if (IsOptional(key.Facet, optionalFacets)) continue;
            if (!snapshot.Resources.TryGetValue(key, out var resource) || !IsResolved(resource)) return false;
        }
        return true;
    }

    public static bool DemandedResolved<T>(QuerySnapshot<T> snapshot) => DemandedResolved(snapshot, PresentationFacets);

    internal static bool IsOptional(FacetKind facet, IReadOnlyList<FacetKind> optionalFacets)
    {
        for (int i = 0; i < optionalFacets.Count; i++)
            if (optionalFacets[i] == facet) return true;
        return false;
    }
}

/// <summary>
/// The detail page's (playlist, album, liked, show) boundary: the membership replica is the only thing that may
/// hold the skeleton. Resident identities paint immediately; members whose identity is still unknown render as
/// loading rows and fill in when their fetch lands. Waiting for every identity (or a header revalidation) kept a
/// fully cached playlist off screen for the length of a network round trip.
/// </summary>
public static class DetailPageReadiness
{
    public static bool InitialLoadComplete<T>(QuerySnapshot<T> snapshot)
    {
        if (snapshot.Revision == 0) return false;
        if (snapshot.Status.Superseded) return false;
        if (snapshot.Demanded.Count == 0) return false;
        return MembershipKnown(snapshot.Value);
    }

    /// <summary>The list's membership must be known before "every demanded row identity resolved" means anything:
    /// a playlist whose header is cached reads with two demanded keys and no rows, which would reveal an empty list
    /// that then fills in. Unknown membership is not an empty list.</summary>
    public static bool MembershipKnown<T>(T value) => value switch
    {
        Playlist playlist => playlist.MembershipLoaded,
        Album album => album.Tracks is not null,
        Show show => show.Episodes is not null,
        _ => true,
    };
}
