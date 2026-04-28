using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Service for fetching artist data with caching and resilience.
/// ViewModels depend on this interface — never on ISession or IPathfinderClient directly.
/// </summary>
public interface IArtistService
{
    Task<ArtistOverviewResult> GetOverviewAsync(string artistUri, CancellationToken ct = default);
    Task<List<ArtistReleaseResult>> GetDiscographyAllAsync(string artistUri, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<List<ArtistReleaseResult>> GetDiscographyPageAsync(string artistUri, string type, int offset, int limit = 20, CancellationToken ct = default);
    Task<List<ArtistTopTrackResult>> GetExtendedTopTracksAsync(string artistUri, CancellationToken ct = default);

    /// <summary>
    /// Resolves cover-art URLs for the given track URIs via the extended-metadata
    /// pipeline. Used to backfill <see cref="ArtistTopTrackResult.AlbumImageUrl"/>
    /// for tracks where Spotify's getArtistOverview GraphQL response omitted
    /// <c>albumOfTrack.coverArt</c> (this happens unpredictably for many tracks).
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetTrackImagesAsync(
        IReadOnlyList<string> trackUris, CancellationToken ct = default);
}

// ── Domain result types (clean boundary — no Pathfinder types leak to ViewModel) ──

public sealed record ArtistOverviewResult
{
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? HeaderImageUrl { get; init; }
    /// <summary>
    /// First gallery shot (landscape-ish artist photo). Fallback for surfaces that
    /// want a hero backdrop but the artist has no editorial HeaderImageUrl.
    /// </summary>
    public string? GalleryHeroUrl { get; init; }
    public string? HeroColorHex { get; init; }
    /// <summary>
    /// Spotify-extracted palette for the artist hero (3 contrast tiers). Null when
    /// the API didn't return a visualIdentity block. Same shape as the concert-page
    /// palette so the two page types can share styling logic.
    /// </summary>
    public ArtistPalette? Palette { get; init; }
    public long MonthlyListeners { get; init; }
    public long Followers { get; init; }
    public string? Biography { get; init; }
    public bool IsVerified { get; init; }

    // Latest release
    public ArtistLatestReleaseResult? LatestRelease { get; init; }

    // Collections (first page from overview)
    public required List<ArtistTopTrackResult> TopTracks { get; init; }
    public required List<ArtistReleaseResult> Albums { get; init; }
    public required List<ArtistReleaseResult> Singles { get; init; }
    public required List<ArtistReleaseResult> Compilations { get; init; }
    public required List<RelatedArtistResult> RelatedArtists { get; init; }

    // Total counts for pagination
    public int AlbumsTotalCount { get; init; }
    public int SinglesTotalCount { get; init; }
    public int CompilationsTotalCount { get; init; }

    // Pinned item + Watch feed
    public ArtistPinnedItemResult? PinnedItem { get; init; }
    public ArtistWatchFeedResult? WatchFeed { get; init; }

    // Concerts
    public required List<ArtistConcertResult> Concerts { get; init; }

    // Connect & Markets — surfaced from Pathfinder visualIdentity / stats /
    // externalLinks blocks. Empty (not null) when the API didn't return them.
    public List<ArtistSocialLinkResult> ExternalLinks { get; init; } = new();
    public List<ArtistTopCityResult> TopCities { get; init; } = new();

    // Gallery — landscape/portrait photos of the artist (multiple resolutions).
    public List<string> GalleryPhotos { get; init; } = new();
}

public sealed record ArtistSocialLinkResult
{
    public required string Name { get; init; }
    public required string Url { get; init; }
}

public sealed record ArtistTopCityResult
{
    public required string City { get; init; }
    public string? Country { get; init; }
    public long NumberOfListeners { get; init; }
}

public sealed record ArtistPalette
{
    public ArtistPaletteTier? HighContrast { get; init; }    // saturated dark bg
    public ArtistPaletteTier? HigherContrast { get; init; }  // darkest bg
    public ArtistPaletteTier? MinContrast { get; init; }     // light / pastel bg
}

public sealed record ArtistPaletteTier
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

public sealed record ArtistLatestReleaseResult
{
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? Uri { get; init; }
    public string? Type { get; init; }
    public int TrackCount { get; init; }
    public string? FormattedDate { get; init; }
}

public sealed record ArtistTopTrackResult
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Uri { get; init; }
    public string? AlbumImageUrl { get; init; }
    public string? AlbumUri { get; init; }
    public string? AlbumName { get; init; }
    public System.TimeSpan Duration { get; init; }
    public long PlayCount { get; init; }
    public string? ArtistNames { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; }
    public bool HasVideo { get; init; }
}

public sealed record ArtistReleaseResult
{
    public required string Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public required string Type { get; init; }
    public string? ImageUrl { get; init; }
    public System.DateTimeOffset ReleaseDate { get; init; }
    public int TrackCount { get; init; }
    public string? Label { get; init; }
    public int Year { get; init; }
}

public sealed record RelatedArtistResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed record ArtistPinnedItemResult
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Comment { get; init; }
    public string? ImageUrl { get; init; }
    public string? BackgroundImageUrl { get; init; }
    public string? Uri { get; init; }
    public string? Type { get; init; }
}

public sealed record ArtistWatchFeedResult
{
    public string? ThumbnailUrl { get; init; }
    public string? VideoUrl { get; init; }
}

public sealed record ArtistConcertResult
{
    public string? Title { get; init; }
    public string? Venue { get; init; }
    public string? City { get; init; }
    public System.DateTimeOffset Date { get; init; }
    public bool IsFestival { get; init; }
    public string? Uri { get; init; }
    public bool IsNearUser { get; init; }
}
