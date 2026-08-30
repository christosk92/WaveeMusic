using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>Spotify's Pathfinder implementation of the provider-neutral concert contract.</summary>
public sealed class SpotifyConcertService : IConcertService
{
    readonly IConcertPathfinder _pathfinder;

    public SpotifyConcertService(IConcertPathfinder pathfinder) =>
        _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));

    public async Task<ArtistConcertSchedule?> GetArtistScheduleAsync(string artistUri, string? geoHash = null,
        bool includeNearby = true, CancellationToken cancellationToken = default)
    {
        RequireValue(artistUri, nameof(artistUri));
        using var document = await QueryAsync(PathfinderOps.ArtistConcerts, PathfinderOps.ArtistConcertsHash,
            writer => ConcertPathfinderRequests.WriteArtistConcerts(writer, artistUri, geoHash, includeNearby),
            cancellationToken).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapArtistSchedule(document.RootElement);
    }

    public async Task<IReadOnlyList<ConcertConcept>> GetConceptsAsync(string geoHash,
        string? selectedConceptUri = null, CancellationToken cancellationToken = default)
    {
        geoHash ??= string.Empty;
        using var document = await QueryAsync(PathfinderOps.ConcertConcepts, PathfinderOps.ConcertConceptsHash,
            writer => ConcertPathfinderRequests.WriteConcepts(writer, geoHash, selectedConceptUri),
            cancellationToken).ConfigureAwait(false);
        return document is null ? Array.Empty<ConcertConcept>() :
            ConcertPathfinderMapper.MapConcepts(document.RootElement);
    }

    public async Task<ConcertFeedPage?> GetFeedAsync(ConcertFeedQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var document = await QueryAsync(PathfinderOps.ConcertFeed, PathfinderOps.ConcertFeedHash,
            writer => ConcertPathfinderRequests.WriteFeed(writer, query), cancellationToken).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapFeed(document.RootElement);
    }

    public async Task<int?> GetFeedCountAsync(ConcertFeedQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var document = await QueryAsync(PathfinderOps.ConcertCount, PathfinderOps.ConcertCountHash,
            writer => ConcertPathfinderRequests.WriteFeedCount(writer, query), cancellationToken,
            TimeSpan.Zero).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapFeedCount(document.RootElement);
    }

    public async Task<ConcertDetails?> GetDetailsAsync(string concertUri, bool authenticated = true,
        CancellationToken cancellationToken = default)
    {
        RequireValue(concertUri, nameof(concertUri));
        using var document = await QueryAsync(PathfinderOps.Concert, PathfinderOps.ConcertHash,
            writer => ConcertPathfinderRequests.WriteConcert(writer, concertUri, authenticated),
            cancellationToken).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapDetails(document.RootElement);
    }

    public async Task<ConcertPlace?> GetUserLocationAsync(CancellationToken cancellationToken = default)
    {
        using var document = await QueryAsync(PathfinderOps.UserLocation, PathfinderOps.UserLocationHash, null,
            cancellationToken, TimeSpan.Zero).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapUserLocation(document.RootElement);
    }

    public async Task<ConcertPlace?> GetArtistPageLocationAsync(CancellationToken cancellationToken = default)
    {
        using var document = await QueryAsync(PathfinderOps.ArtistConcertsPageLocation,
            PathfinderOps.ArtistConcertsPageLocationHash, null, cancellationToken, TimeSpan.Zero).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapUserLocation(document.RootElement);
    }

    public async Task<bool?> IsUserLocationInferredAsync(CancellationToken cancellationToken = default)
    {
        using var document = await QueryAsync(PathfinderOps.InferredUserLocation,
            PathfinderOps.InferredUserLocationHash, null, cancellationToken, TimeSpan.Zero).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapIsInferred(document.RootElement);
    }

    public async Task<IReadOnlyList<ConcertPlace>> SearchLocationsAsync(string query,
        CancellationToken cancellationToken = default)
    {
        query ??= string.Empty;
        using var document = await QueryAsync(PathfinderOps.SearchConcertLocations,
            PathfinderOps.SearchConcertLocationsHash,
            writer => ConcertPathfinderRequests.WriteSearchLocations(writer, query), cancellationToken,
            TimeSpan.Zero).ConfigureAwait(false);
        return document is null ? Array.Empty<ConcertPlace>() :
            ConcertPathfinderMapper.MapLocations(document.RootElement);
    }

    public async Task<IReadOnlyList<ConcertPlace>> ReverseLocationAsync(GeoCoordinates coordinates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        using var document = await QueryAsync(PathfinderOps.ConcertLocationsByLatLon,
            PathfinderOps.ConcertLocationsByLatLonHash,
            writer => ConcertPathfinderRequests.WriteReverseLocation(writer, coordinates), cancellationToken,
            TimeSpan.Zero).ConfigureAwait(false);
        return document is null ? Array.Empty<ConcertPlace>() :
            ConcertPathfinderMapper.MapLocations(document.RootElement);
    }

    public async Task<ConcertLocationSnapshot?> GetLocationDetailsAsync(string? placeId, bool isAnonymous = false,
        CancellationToken cancellationToken = default)
    {
        using var document = await QueryAsync(PathfinderOps.ConcertLocationDetails,
            PathfinderOps.ConcertLocationDetailsHash,
            writer => ConcertPathfinderRequests.WriteLocationDetails(writer, placeId, isAnonymous),
            cancellationToken).ConfigureAwait(false);
        return document is null ? null : ConcertPathfinderMapper.MapLocationSnapshot(document.RootElement);
    }

    public async Task<bool> SaveLocationAsync(string placeId, CancellationToken cancellationToken = default)
    {
        RequireValue(placeId, nameof(placeId));
        using var document = await QueryAsync(PathfinderOps.SaveLocation, PathfinderOps.SaveLocationHash,
            writer => ConcertPathfinderRequests.WriteSaveLocation(writer, placeId), cancellationToken,
            TimeSpan.Zero).ConfigureAwait(false);
        return document is not null && ConcertPathfinderMapper.MapSaveLocation(document.RootElement);
    }

    Task<JsonDocument?> QueryAsync(string operation, string hash, Action<Utf8JsonWriter>? variables,
        CancellationToken cancellationToken, TimeSpan? ttl = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _pathfinder.QueryAsync(operation, hash, variables, PathfinderClient.Platform.WebPlayer,
            cancellationToken, ttl);
    }

    static void RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
    }
}
