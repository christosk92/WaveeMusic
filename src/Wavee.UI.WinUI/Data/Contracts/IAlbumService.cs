using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Service for fetching album data with caching and resilience.
/// Reusable from artist page, album detail page, search results, etc.
/// </summary>
public interface IAlbumService
{
    Task<List<AlbumTrackDto>> GetTracksAsync(string albumUri, CancellationToken ct = default);
    Task<AlbumDetailResult> GetDetailAsync(string albumUri, CancellationToken ct = default);
    Task<List<AlbumMerchItemResult>> GetMerchAsync(string albumUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches similar-album recommendations seeded from a track URI. Backed by
    /// the <c>similarAlbumsBasedOnThisTrack</c> persisted query. Used to populate
    /// the AlbumPage "For this mood" / "Similar albums" shelf — seed with the
    /// album's most-played track (fall back to track[0] if play counts aren't yet
    /// loaded).
    /// </summary>
    Task<List<AlbumSimilarResult>> GetSimilarAlbumsAsync(
        string trackUri, int limit = 24, CancellationToken ct = default);

    /// <summary>
    /// Fetches a track-level music-video association. Returns the video-track URI
    /// when the source track has at least one entry in <c>videoAssociations</c>;
    /// returns null otherwise. Drives the "Watch the official video" promotion on
    /// the AlbumPage for 1-track singles.
    /// </summary>
    Task<string?> GetMusicVideoUriAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches an artist's bio excerpt + related artists for the AlbumPage's
    /// mini "About the artist" card and "Fans also like" pill list. Reuses the
    /// <c>queryArtistOverview</c> response — same query that backs ArtistPage.
    /// </summary>
    Task<AlbumArtistContextResult> GetArtistContextAsync(
        string artistUri, CancellationToken ct = default);
}

public sealed record AlbumSimilarResult
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? ArtistName { get; init; }
    public string? ArtistUri { get; init; }
    /// <summary>"ALBUM" | "SINGLE" | "EP" | "COMPILATION" — drives the tile type chip.</summary>
    public string? Type { get; init; }
    public int Year { get; init; }
}

public sealed record AlbumArtistContextResult
{
    public string? BioExcerpt { get; init; }
    public required List<RelatedArtistResult> SimilarArtists { get; init; }
}

public sealed record AlbumDetailResult
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? Type { get; init; }
    public string? Label { get; init; }
    public string? CoverArtUrl { get; init; }
    public string? ColorDarkHex { get; init; }
    public string? ColorLightHex { get; init; }
    public string? ColorRawHex { get; init; }
    public DateTimeOffset ReleaseDate { get; init; }
    public bool IsSaved { get; init; }
    public bool IsPreRelease { get; init; }
    /// <summary>
    /// When IsPreRelease is true, the moment the album becomes available. Used by
    /// the pre-release banner to format "Coming {weekday} at {time}".
    /// </summary>
    public DateTimeOffset? PreReleaseEndDateTime { get; init; }
    public required List<AlbumCopyrightResult> Copyrights { get; init; }
    public required List<AlbumArtistResult> Artists { get; init; }
    public required List<AlbumTrackDto> Tracks { get; init; }
    public required List<AlbumRelatedResult> MoreByArtist { get; init; }
    /// <summary>
    /// Alternate editions of THIS album (deluxe / remaster / anniversary / instrumental).
    /// Distinct from MoreByArtist (which is the artist's other albums). Empty when the
    /// API doesn't return any.
    /// </summary>
    public required List<AlbumAlternateReleaseResult> AlternateReleases { get; init; }
    public int TotalTracks { get; init; }
    public int DiscCount { get; init; }
    public string? ShareUrl { get; init; }
    /// <summary>
    /// Spotify-extracted palette for the album cover (3 contrast tiers). Null when
    /// the API doesn't return a visualIdentity block. Same shape as ArtistPalette /
    /// ConcertArtistPalette so the page can use the concert's theme-aware logic.
    /// </summary>
    public AlbumPalette? Palette { get; init; }
}

public sealed record AlbumAlternateReleaseResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? CoverArtUrl { get; init; }
    public int Year { get; init; }
    public string? Type { get; init; } // "ALBUM" | "SINGLE" | "COMPILATION" | …
}

public sealed record AlbumPalette
{
    public AlbumPaletteTier? HighContrast { get; init; }    // saturated dark bg
    public AlbumPaletteTier? HigherContrast { get; init; }  // darkest bg
    public AlbumPaletteTier? MinContrast { get; init; }     // light / pastel bg
}

public sealed record AlbumPaletteTier
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

public sealed record AlbumCopyrightResult
{
    public string? Text { get; init; }
    public string? Type { get; init; }
}

public sealed record AlbumArtistResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed record AlbumRelatedResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? ImageUrl { get; init; }
    public int Year { get; init; }
}

public sealed record AlbumMerchItemResult
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? Price { get; init; }
    public string? ShopUrl { get; init; }
}

public sealed record AlbumTrackResult
{
    public required string Id { get; init; }
    public string? Uid { get; init; }
    public string? Title { get; init; }
    public string? Uri { get; init; }
    public TimeSpan Duration { get; init; }
    public long PlayCount { get; init; }
    public string? ArtistNames { get; init; }
    public List<TrackArtistRef> Artists { get; init; } = [];
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; }
    public bool IsSaved { get; init; }
    public bool HasVideo { get; init; }
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; }
}

/// <summary>
/// Lightweight artist reference carried by track-level data. Preserves the
/// per-track artist list with URIs so multi-artist tracks (collabs, soundtracks)
/// render every contributor as an independently-clickable hyperlink.
/// </summary>
public sealed record TrackArtistRef
{
    public required string Id { get; init; }
    public required string Uri { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// Projection of an album-billed artist for the header names line. Carries
/// <see cref="IsFirst"/> so the comma separator preceding each entry can be
/// hidden on the first item without a converter that walks the parent list.
/// </summary>
public sealed record HeaderArtistLink
{
    public required string Name { get; init; }
    public required string Uri { get; init; }
    public required bool IsFirst { get; init; }
}
