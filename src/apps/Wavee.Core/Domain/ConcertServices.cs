namespace Wavee.Core;

/// <summary>A half-open filter window on the wire as {from,to} ISO dates. Both ends inclusive (a single-day filter
/// sets From == To).</summary>
public sealed record ConcertDateRange(DateOnly From, DateOnly To);

/// <summary>Provider-neutral input for one Concert Hub page. Location identifiers remain opaque; adapters translate
/// them to their own wire fields. A null radius asks the provider to use its default. An empty/null concept set means
/// "all genres"; multiple concepts ask the provider for one per-genre carousel each.</summary>
public sealed record ConcertFeedQuery(
    ConcertPlace? Location = null,
    IReadOnlyList<string>? ConceptUris = null,
    int? RadiusKm = 100,
    ConcertDateRange? DateRange = null,
    string? PaginationKey = null);

/// <summary>Candidate locations for a requested place plus the account's currently saved location.</summary>
public sealed record ConcertLocationSnapshot(
    IReadOnlyList<ConcertPlace> Matches,
    ConcertPlace? SavedLocation = null);

/// <summary>The application-facing concert discovery contract. Implementations may use Pathfinder, another provider,
/// or deterministic local data without changing route or page code.</summary>
public interface IConcertService
{
    Task<ArtistConcertSchedule?> GetArtistScheduleAsync(string artistUri, string? geoHash = null,
        bool includeNearby = true, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConcertConcept>> GetConceptsAsync(string geoHash, string? selectedConceptUri = null,
        CancellationToken cancellationToken = default);

    Task<ConcertFeedPage?> GetFeedAsync(ConcertFeedQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>The number of events the given filter would return, without fetching the feed itself. Null when the
    /// provider does not answer (offline/unauthenticated). Never cached — a live filter preview must not replay stale.</summary>
    Task<int?> GetFeedCountAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default);

    Task<ConcertDetails?> GetDetailsAsync(string concertUri, bool authenticated = true,
        CancellationToken cancellationToken = default);

    Task<ConcertPlace?> GetUserLocationAsync(CancellationToken cancellationToken = default);
    Task<ConcertPlace?> GetArtistPageLocationAsync(CancellationToken cancellationToken = default);
    Task<bool?> IsUserLocationInferredAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConcertPlace>> SearchLocationsAsync(string query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConcertPlace>> ReverseLocationAsync(GeoCoordinates coordinates,
        CancellationToken cancellationToken = default);

    Task<ConcertLocationSnapshot?> GetLocationDetailsAsync(string? placeId, bool isAnonymous = false,
        CancellationToken cancellationToken = default);

    Task<bool> SaveLocationAsync(string placeId, CancellationToken cancellationToken = default);
}

/// <summary>A stable identity whose live provider (a Pathfinder adapter) is installed after login without rebuilding
/// consumers. Mirrors <see cref="SwitchableWhatsNewService"/>; the concert contract is pure request/response, so the
/// wrapper simply forwards every read/write to the current inner. Offline it is the permanently-offline
/// <see cref="NullConcertService"/>.</summary>
public sealed class SwitchableConcertService : IConcertService
{
    IConcertService _inner;

    public SwitchableConcertService(IConcertService inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public void SetInner(IConcertService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Volatile.Write(ref _inner, inner);
    }

    IConcertService Current => Volatile.Read(ref _inner);

    public Task<ArtistConcertSchedule?> GetArtistScheduleAsync(string artistUri, string? geoHash = null,
        bool includeNearby = true, CancellationToken cancellationToken = default) =>
        Current.GetArtistScheduleAsync(artistUri, geoHash, includeNearby, cancellationToken);

    public Task<IReadOnlyList<ConcertConcept>> GetConceptsAsync(string geoHash, string? selectedConceptUri = null,
        CancellationToken cancellationToken = default) =>
        Current.GetConceptsAsync(geoHash, selectedConceptUri, cancellationToken);

    public Task<ConcertFeedPage?> GetFeedAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default) =>
        Current.GetFeedAsync(query, cancellationToken);

    public Task<int?> GetFeedCountAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default) =>
        Current.GetFeedCountAsync(query, cancellationToken);

    public Task<ConcertDetails?> GetDetailsAsync(string concertUri, bool authenticated = true,
        CancellationToken cancellationToken = default) =>
        Current.GetDetailsAsync(concertUri, authenticated, cancellationToken);

    public Task<ConcertPlace?> GetUserLocationAsync(CancellationToken cancellationToken = default) =>
        Current.GetUserLocationAsync(cancellationToken);

    public Task<ConcertPlace?> GetArtistPageLocationAsync(CancellationToken cancellationToken = default) =>
        Current.GetArtistPageLocationAsync(cancellationToken);

    public Task<bool?> IsUserLocationInferredAsync(CancellationToken cancellationToken = default) =>
        Current.IsUserLocationInferredAsync(cancellationToken);

    public Task<IReadOnlyList<ConcertPlace>> SearchLocationsAsync(string query,
        CancellationToken cancellationToken = default) =>
        Current.SearchLocationsAsync(query, cancellationToken);

    public Task<IReadOnlyList<ConcertPlace>> ReverseLocationAsync(GeoCoordinates coordinates,
        CancellationToken cancellationToken = default) =>
        Current.ReverseLocationAsync(coordinates, cancellationToken);

    public Task<ConcertLocationSnapshot?> GetLocationDetailsAsync(string? placeId, bool isAnonymous = false,
        CancellationToken cancellationToken = default) =>
        Current.GetLocationDetailsAsync(placeId, isAnonymous, cancellationToken);

    public Task<bool> SaveLocationAsync(string placeId, CancellationToken cancellationToken = default) =>
        Current.SaveLocationAsync(placeId, cancellationToken);
}

/// <summary>Offline/fake fallback: reads return null/empty (never throw), and a save reports it did not persist. No
/// silent success — <see cref="SaveLocationAsync"/> returns false rather than pretending a write reached a backend.</summary>
public sealed class NullConcertService : IConcertService
{
    static readonly IReadOnlyList<ConcertConcept> NoConcepts = Array.Empty<ConcertConcept>();
    static readonly IReadOnlyList<ConcertPlace> NoPlaces = Array.Empty<ConcertPlace>();

    public Task<ArtistConcertSchedule?> GetArtistScheduleAsync(string artistUri, string? geoHash = null,
        bool includeNearby = true, CancellationToken cancellationToken = default) =>
        Task.FromResult<ArtistConcertSchedule?>(null);

    public Task<IReadOnlyList<ConcertConcept>> GetConceptsAsync(string geoHash, string? selectedConceptUri = null,
        CancellationToken cancellationToken = default) => Task.FromResult(NoConcepts);

    public Task<ConcertFeedPage?> GetFeedAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<ConcertFeedPage?>(null);

    public Task<int?> GetFeedCountAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);

    public Task<ConcertDetails?> GetDetailsAsync(string concertUri, bool authenticated = true,
        CancellationToken cancellationToken = default) => Task.FromResult<ConcertDetails?>(null);

    public Task<ConcertPlace?> GetUserLocationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ConcertPlace?>(null);

    public Task<ConcertPlace?> GetArtistPageLocationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ConcertPlace?>(null);

    public Task<bool?> IsUserLocationInferredAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<bool?>(null);

    public Task<IReadOnlyList<ConcertPlace>> SearchLocationsAsync(string query,
        CancellationToken cancellationToken = default) => Task.FromResult(NoPlaces);

    public Task<IReadOnlyList<ConcertPlace>> ReverseLocationAsync(GeoCoordinates coordinates,
        CancellationToken cancellationToken = default) => Task.FromResult(NoPlaces);

    public Task<ConcertLocationSnapshot?> GetLocationDetailsAsync(string? placeId, bool isAnonymous = false,
        CancellationToken cancellationToken = default) => Task.FromResult<ConcertLocationSnapshot?>(null);

    public Task<bool> SaveLocationAsync(string placeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
