using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

public sealed class LocationService : ILocationService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly ILogger? _logger;
    private bool _fetched;

    public string? CurrentCity { get; private set; }

    public LocationService(IPathfinderClient pathfinder, ILogger<LocationService>? logger = null)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    public async Task<string?> GetUserCityAsync(CancellationToken ct = default)
    {
        if (_fetched) return CurrentCity;
        _fetched = true;

        try
        {
            var loc = await _pathfinder.GetUserLocationAsync(ct);
            CurrentCity = loc.Data?.Me?.Profile?.Location?.Name;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch user location");
        }

        return CurrentCity;
    }

    public async Task<List<LocationSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var response = await _pathfinder.SearchConcertLocationsAsync(query, ct);
        return response.Data?.ConcertLocations?.Items?
            .Select(loc => new LocationSearchResult
            {
                Name = loc.Name,
                FullName = loc.FullName ?? loc.Name,
                GeonameId = loc.GeonameId,
                Country = loc.Country
            }).ToList() ?? [];
    }

    public async Task<string?> SaveByGeonameIdAsync(string geonameId, string? cityName = null, CancellationToken ct = default)
    {
        await _pathfinder.SaveLocationAsync(geonameId, ct);
        if (cityName != null)
            CurrentCity = cityName;
        _fetched = false; // invalidate cache so next GetUserCityAsync re-fetches
        return CurrentCity;
    }

    public async Task<LocationSearchResult?> SearchByCoordinatesAsync(double lat, double lon, CancellationToken ct = default)
    {
        var locResponse = await _pathfinder.GetConcertLocationByLatLonAsync(lat, lon, ct);
        var firstLoc = locResponse.Data?.ConcertLocations?.Items?.FirstOrDefault();
        if (firstLoc?.GeonameId == null) return null;

        return new LocationSearchResult
        {
            Name = firstLoc.Name,
            FullName = firstLoc.FullName ?? firstLoc.Name,
            GeonameId = firstLoc.GeonameId,
            Country = firstLoc.Country
        };
    }

    public bool IsNearUser(string? concertCity)
    {
        return !string.IsNullOrEmpty(CurrentCity)
            && string.Equals(concertCity, CurrentCity, StringComparison.OrdinalIgnoreCase);
    }
}
