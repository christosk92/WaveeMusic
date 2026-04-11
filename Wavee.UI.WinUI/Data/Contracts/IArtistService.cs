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
}

// ── Domain result types (clean boundary — no Pathfinder types leak to ViewModel) ──

public sealed record ArtistOverviewResult
{
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? HeaderImageUrl { get; init; }
    public string? HeroColorHex { get; init; }
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
