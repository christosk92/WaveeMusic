using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface IConcertService
{
    Task<ConcertDetailResult> GetDetailAsync(string concertUri, CancellationToken ct = default);
}

public sealed record ConcertDetailResult
{
    public string? Title { get; init; }
    public string? Uri { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? Venue { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public bool IsFestival { get; init; }
    public bool IsSaved { get; init; }
    public string? AgeRestriction { get; init; }
    public string? ShowTime { get; init; } // e.g. "8:00 PM"
    public string? FullLocation { get; init; } // e.g. "Subterranean, Chicago, US"
    public required List<ConcertArtistResult> Artists { get; init; }
    public required List<ConcertOfferResult> Offers { get; init; }
    public required List<ConcertRelatedResult> RelatedConcerts { get; init; }
    public required List<string> Genres { get; init; }
}

public sealed record ConcertArtistResult
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? AvatarUrl { get; init; }
    public string? HeaderImageUrl { get; init; }
    public int UpcomingConcertCount { get; init; }
    public required List<ConcertPopularAlbumResult> PopularAlbums { get; init; }
}

public sealed record ConcertPopularAlbumResult
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? CoverArtUrl { get; init; }
    public string? ArtistName { get; init; }
}

public sealed record ConcertOfferResult
{
    public string? ProviderName { get; init; }
    public string? ProviderImageUrl { get; init; }
    public string? Url { get; init; }
    public string? SaleType { get; init; }
}

public sealed record ConcertRelatedResult
{
    public string? Title { get; init; }
    public string? Uri { get; init; }
    public string? City { get; init; }
    public string? Venue { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? ArtistName { get; init; }
    public string? ArtistAvatarUrl { get; init; }
}
