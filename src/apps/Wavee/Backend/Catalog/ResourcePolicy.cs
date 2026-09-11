using System;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

public static class ResourcePolicy
{
    public static TimeSpan FreshFor(FacetKind facet, TimeSpan? providerTtl = null, bool absent = false)
    {
        if (absent) return TimeSpan.FromSeconds(Math.Clamp((providerTtl ?? TimeSpan.FromHours(24)).TotalSeconds, 60, 24 * 60 * 60));
        if (facet == FacetKind.PlaylistRevision) return TimeSpan.FromSeconds(5);
        if (facet == FacetKind.PlaylistHeader) return TimeSpan.FromMinutes(5);
        if (providerTtl is { } ttl)
            return TimeSpan.FromSeconds(Math.Clamp(ttl.TotalSeconds, 60, 24 * 60 * 60));
        return facet switch
        {
            FacetKind.TrackIdentity or FacetKind.EpisodeIdentity or FacetKind.AlbumIdentity
                or FacetKind.ArtistIdentity or FacetKind.ShowIdentity
                or FacetKind.UserIdentity => TimeSpan.FromHours(1),
            FacetKind.AlbumDetail => TimeSpan.FromMinutes(10),
            FacetKind.ArtistOverview => TimeSpan.FromHours(12),
            _ => TimeSpan.FromHours(6),
        };
    }

    public static DateTimeOffset ExpiresAt(CatalogValue value, DateTimeOffset now, TimeSpan? providerTtl = null)
    {
        var expiry = now + FreshFor(value.Facet, providerTtl);
        return value is PlaylistHeaderValue { NextUpdateAt: { } next } && next > now && next < expiry
            ? next : expiry;
    }

    public static TimeSpan? RetryDelay(ResourceError? error, int completedAttempts)
    {
        // The HTTP middleware alone handles 429. Retrying that failure here multiplies its attempts.
        if (error is not { Kind: ResourceErrorKind.Transport } || error.StatusCode == 429
            || (error.StatusCode is { } status && (status < 500 || status > 599))) return null;
        return completedAttempts switch
        {
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(10),
            3 => TimeSpan.FromSeconds(30),
            _ => null,
        };
    }
}
