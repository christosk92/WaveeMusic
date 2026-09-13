using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The narrow Pathfinder seam required by concert discovery. It keeps the adapter fixture-testable without
/// exposing transport or cache implementation details to Core.</summary>
public interface IConcertPathfinder
{
    Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform,
        CancellationToken cancellationToken, TimeSpan? ttl = null);
}

/// <summary>Captured concert-operation variable writers. Property names and explicit nulls are part of the wire
/// contract and are deliberately centralized here.</summary>
public static class ConcertPathfinderRequests
{
    public static void WriteArtistConcerts(Utf8JsonWriter writer, string artistUri, string? geoHash,
        bool includeNearby)
    {
        writer.WriteString("artistUri", artistUri);
        WriteNullableString(writer, "geoHash", geoHash);
        writer.WriteBoolean("includeNearby", includeNearby);
    }

    public static void WriteConcepts(Utf8JsonWriter writer, string geoHash, string? conceptUri)
    {
        writer.WriteString("geohash", geoHash);
        WriteNullableString(writer, "conceptUri", conceptUri);
    }

    public static void WriteFeed(Utf8JsonWriter writer, ConcertFeedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.RadiusKm is < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Concert radius cannot be negative.");

        string? placeId = EmptyToNull(query.Location?.Id);
        WriteNullableString(writer, "geoHash", placeId is null ? query.Location?.GeoHash : null);
        WriteNullableString(writer, "geonameId", placeId);
        WriteDateRange(writer, query.DateRange);
        WriteConceptUris(writer, query.ConceptUris);
        if (query.RadiusKm is { } radius)
            writer.WriteNumber("radiusInKm", radius);
        else
            writer.WriteNull("radiusInKm");
        WriteNullableString(writer, "paginationKey", query.PaginationKey);
    }

    // concertCount reuses the feed variables minus paginationKey/geoHash: the capture sent exactly
    // {geonameId, radiusInKm, dateRange, conceptUris} (in that order) while dragging the radius control.
    public static void WriteFeedCount(Utf8JsonWriter writer, ConcertFeedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.RadiusKm is < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Concert radius cannot be negative.");

        WriteNullableString(writer, "geonameId", EmptyToNull(query.Location?.Id));
        if (query.RadiusKm is { } radius)
            writer.WriteNumber("radiusInKm", radius);
        else
            writer.WriteNull("radiusInKm");
        WriteDateRange(writer, query.DateRange);
        WriteConceptUris(writer, query.ConceptUris);
    }

    public static void WriteLocationDetails(Utf8JsonWriter writer, string? placeId, bool isAnonymous)
    {
        WriteNullableString(writer, "geonameId", placeId);
        writer.WriteBoolean("isAnonymous", isAnonymous);
    }

    public static void WriteSearchLocations(Utf8JsonWriter writer, string query) =>
        writer.WriteString("query", query);

    public static void WriteReverseLocation(Utf8JsonWriter writer, GeoCoordinates coordinates)
    {
        writer.WriteNumber("lat", coordinates.Latitude);
        writer.WriteNumber("lon", coordinates.Longitude);
    }

    public static void WriteSaveLocation(Utf8JsonWriter writer, string placeId) =>
        writer.WriteString("geonameId", placeId);

    public static void WriteConcert(Utf8JsonWriter writer, string uri, bool authenticated)
    {
        writer.WriteString("uri", uri);
        writer.WriteBoolean("authenticated", authenticated);
    }

    static void WriteDateRange(Utf8JsonWriter writer, ConcertDateRange? range)
    {
        if (range is null)
        {
            writer.WriteNull("dateRange");
            return;
        }
        writer.WriteStartObject("dateRange");
        writer.WriteString("from", range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteString("to", range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    static void WriteConceptUris(Utf8JsonWriter writer, IReadOnlyList<string>? concepts)
    {
        writer.WritePropertyName("conceptUris");
        if (concepts is not { Count: > 0 })
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (string uri in concepts)
            writer.WriteStringValue(uri);
        writer.WriteEndArray();
    }

    static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        value = EmptyToNull(value);
        if (value is null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }

    static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
