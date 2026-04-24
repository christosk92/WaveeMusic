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
    /// <summary>Editorial playlists related to the headliner (e.g. "This Is …"). Empty when
    /// the API didn't return a relatedContent.featuringV2 block.</summary>
    public required List<ConcertFeaturedPlaylistResult> FeaturedPlaylists { get; init; }
}

public sealed record ConcertFeaturedPlaylistResult
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? OwnerName { get; init; }
}

public sealed record ConcertArtistResult
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? AvatarUrl { get; init; }
    public string? HeaderImageUrl { get; init; }
    public int UpcomingConcertCount { get; init; }
    public required List<ConcertPopularAlbumResult> PopularAlbums { get; init; }
    /// <summary>
    /// Spotify-extracted palette for this artist (from the hero image). Null when the
    /// API doesn't return a visualIdentity block. Each tier has Background/TextBase/
    /// TextBrightAccent/TextSubdued and is pre-computed for dark/darker/light-bg contexts.
    /// </summary>
    public ConcertArtistPalette? Palette { get; init; }
}

public sealed record ConcertArtistPalette
{
    public ConcertPaletteTier? HighContrast { get; init; }    // saturated dark bg
    public ConcertPaletteTier? HigherContrast { get; init; }  // darkest bg
    public ConcertPaletteTier? MinContrast { get; init; }     // light / pastel bg
}

public sealed record ConcertPaletteTier
{
    public byte BackgroundR { get; init; }
    public byte BackgroundG { get; init; }
    public byte BackgroundB { get; init; }
    public byte BackgroundTintedR { get; init; }
    public byte BackgroundTintedG { get; init; }
    public byte BackgroundTintedB { get; init; }
    public byte TextAccentR { get; init; }
    public byte TextAccentG { get; init; }
    public byte TextAccentB { get; init; }
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
