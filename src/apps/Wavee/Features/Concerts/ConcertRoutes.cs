using System;

namespace Wavee.Features.Concerts;

/// <summary>Application route identities for concert discovery. Entity identifiers remain opaque so another provider
/// can use the same navigation surface without adopting Spotify URI parsing.</summary>
public static class ConcertRoutes
{
    public const string Hub = "concerts";
    public const string ArtistSchedulePrefix = "artist-concerts:";
    public const string DetailPrefix = "concert:";

    public static string ArtistSchedule(string artistId) =>
        ArtistSchedulePrefix + Required(artistId, nameof(artistId));

    public static string Detail(string concertId) =>
        DetailPrefix + Required(concertId, nameof(concertId));

    public static bool Is(string routeName) => TryParse(routeName, out _);

    public static bool TryParse(string? routeName, out ConcertRoute destination)
    {
        if (string.Equals(routeName, Hub, StringComparison.Ordinal))
        {
            destination = new ConcertRoute(ConcertRouteKind.Hub, null);
            return true;
        }

        if (TryEntity(routeName, ArtistSchedulePrefix, out var artistId))
        {
            destination = new ConcertRoute(ConcertRouteKind.ArtistSchedule, artistId);
            return true;
        }

        if (TryEntity(routeName, DetailPrefix, out var concertId))
        {
            destination = new ConcertRoute(ConcertRouteKind.Detail, concertId);
            return true;
        }

        destination = default;
        return false;
    }

    static bool TryEntity(string? routeName, string prefix, out string entityId)
    {
        if (routeName is not null && routeName.StartsWith(prefix, StringComparison.Ordinal) &&
            routeName.Length > prefix.Length)
        {
            entityId = routeName[prefix.Length..];
            return !string.IsNullOrWhiteSpace(entityId);
        }
        entityId = string.Empty;
        return false;
    }

    static string Required(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A non-empty concert route identifier is required.", parameterName);
}

public enum ConcertRouteKind { Hub, ArtistSchedule, Detail }

public readonly record struct ConcertRoute(ConcertRouteKind Kind, string? EntityId);
