using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface ILocationService
{
    /// <summary>Current cached city name (null if not yet fetched).</summary>
    string? CurrentCity { get; }

    /// <summary>Fetch user's saved city from Spotify (cached after first call).</summary>
    Task<string?> GetUserCityAsync(CancellationToken ct = default);

    /// <summary>Search concert locations by city name.</summary>
    Task<List<LocationSearchResult>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>Save a location by geonameId. Returns the city name.</summary>
    Task<string?> SaveByGeonameIdAsync(string geonameId, string? cityName = null, CancellationToken ct = default);

    /// <summary>Resolve lat/lon to a location result (for confirmation before saving).</summary>
    Task<LocationSearchResult?> SearchByCoordinatesAsync(double lat, double lon, CancellationToken ct = default);

    /// <summary>Update the "near user" flag on a list of concerts based on current city.</summary>
    bool IsNearUser(string? concertCity);
}

public sealed record LocationSearchResult
{
    public string? Name { get; init; }
    public string? FullName { get; init; }
    public string? GeonameId { get; init; }
    public string? Country { get; init; }

    public override string ToString() => FullName ?? Name ?? "";
}
